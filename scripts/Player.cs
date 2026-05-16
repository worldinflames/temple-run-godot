using Godot;

namespace TempleRun;

public partial class Player : CharacterBody3D
{
	[Signal] public delegate void DiedEventHandler();
	[Signal] public delegate void ScoreChangedEventHandler(double distance, int coins);

	public static readonly float[] Lanes = { -2.2f, 0f, 2.2f };
	private const float LaneSlideSpeed = 10f;   // local-X units per second
	private const float Gravity       = 28f;
	private const float JumpVelocity  = 10.5f;

	[Export] public float ForwardSpeed    { get; set; } = 12f;
	[Export] public float SpeedRampPerSec { get; set; } = 0.08f;
	[Export] public float MaxForwardSpeed { get; set; } = 26f;
	[Export] public NodePath TrackPath    { get; set; } = new();

	public Vector3 RunForward { get; private set; } = new(0f, 0f, 1f);
	public bool IsAlive => _alive;

	private TrackSpawner? _track;
	private int   _laneIndex  = 1;
	private float _smoothLaneX;          // current lateral offset in segment-local X
	private bool  _alive      = true;
	private double _distanceTravelled;
	private int   _coins;
	private double _runPhase;
	private int   _pendingTurn;
	private bool  _jumpFired;
	private int   _dbgFrame;

	// Rising-edge key state
	private bool _prevLeft, _prevRight, _prevJump, _prevQ, _prevE;

	private Node3D _meshRoot = null!;
	private Node3D _armL     = null!;
	private Node3D _armR     = null!;
	private Node3D _legL     = null!;
	private Node3D _legR     = null!;
	private Node3D _torso    = null!;

	// ── Lifecycle ──────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_meshRoot = GetNode<Node3D>("MeshRoot");
		_armL     = GetNode<Node3D>("MeshRoot/LeftArmPivot");
		_armR     = GetNode<Node3D>("MeshRoot/RightArmPivot");
		_legL     = GetNode<Node3D>("MeshRoot/LeftLegPivot");
		_legR     = GetNode<Node3D>("MeshRoot/RightLegPivot");
		_torso    = GetNode<Node3D>("MeshRoot/Torso");

		if (!TrackPath.IsEmpty)
			_track = GetNodeOrNull<TrackSpawner>(TrackPath);

		_smoothLaneX = Lanes[_laneIndex];
		SnapToLaneWorld();
		SyncBaseline();
	}

	/// <summary>Called by Game._Ready() to directly wire the track reference (NodePath resolution unreliable).</summary>
	public void SetTrack(TrackSpawner track) => _track = track;

	public void ConfigureForNewRun()
	{
		_track ??= GetNodeOrNull<TrackSpawner>(TrackPath);
	}

	// ── Compatibility stubs (called by Game.cs) ────────────────────────────

	public void NotifyLaneStep(int delta) { }

	public static bool IsLaneLeftKeyEvent(InputEventKey k)  =>
		k.Keycode == Key.Left  || k.PhysicalKeycode == Key.Left  ||
		k.Keycode == Key.A     || k.PhysicalKeycode == Key.A;

	public static bool IsLaneRightKeyEvent(InputEventKey k) =>
		k.Keycode == Key.Right || k.PhysicalKeycode == Key.Right ||
		k.Keycode == Key.D     || k.PhysicalKeycode == Key.D;

	public int PullTurnRequest()
	{
		if (_pendingTurn == 0) return 0;
		var v = _pendingTurn;
		_pendingTurn = 0;
		return v;
	}

	// ── Main physics loop ──────────────────────────────────────────────────

	public override void _PhysicsProcess(double delta)
	{
		var dt = (float)delta;
		if (!_alive) return;

		var seg = _track?.GetClosestSegment(GlobalPosition);

		// 1. Direction this frame comes from the segment underfoot
		UpdateRunForward(seg);

		// 2. Read input (rising-edge) — also changes _laneIndex
		PollInput(IsInTurnZone(seg));

		// 3. Slide _smoothLaneX toward target lane
		_smoothLaneX = Mathf.MoveToward(_smoothLaneX, Lanes[_laneIndex], LaneSlideSpeed * dt);

		// 4. Speed ramp
		ForwardSpeed = Mathf.Min(MaxForwardSpeed, ForwardSpeed + SpeedRampPerSec * dt);

		// 5. Vertical physics
		var vy = Velocity.Y;
		var wasAirborne = !IsOnFloor();
		if (!IsOnFloor())
			vy -= Gravity * dt;
		else
		{
			vy = 0f;
			if (_jumpFired)
			{
				vy = JumpVelocity;
				_meshRoot.Scale = new Vector3(1f, 1.08f, 1f);
			}
		}

		// 6. MoveAndSlide handles ONLY forward + vertical — no lateral velocity
		Velocity = new Vector3(RunForward.X * ForwardSpeed, vy, RunForward.Z * ForwardSpeed);
		MoveAndSlide();

		// 7. Enforce lane — directly set lateral GlobalPosition after slide
		EnforceLanePosition(seg);

		// 8. Hazard checks
		CheckHazards();

		// 9. Fall death
		if (GlobalPosition.Y < -8f) { Die(); return; }

		// 10. Land squash reset
		if (IsOnFloor() && wasAirborne)
			_meshRoot.Scale = Vector3.One;

		// 11. Cosmetics + scoring
		UpdateRunAnimation(dt);
		UpdateFacing();
		_distanceTravelled += ForwardSpeed * dt;
		EmitSignal(SignalName.ScoreChanged, _distanceTravelled, _coins);
	}

	// ── Movement helpers ───────────────────────────────────────────────────

	private void UpdateRunForward(Node3D? seg)
	{
		if (seg == null) return;
		var f = seg.GlobalTransform.Basis.Z;
		f.Y = 0f;
		if (f.LengthSquared() > 1e-6f)
			RunForward = f.Normalized();
	}

	private bool IsInTurnZone(Node3D? seg)
	{
		if (seg == null) return false;
		var local = seg.GlobalTransform.AffineInverse() * GlobalPosition;
		return local.Z > 2.8f;
	}

	/// <summary>
	/// After MoveAndSlide(), directly reposition the player at the correct lateral
	/// offset (_smoothLaneX) in the current segment's local X axis.
	/// MoveAndSlide owns vertical + forward; this method owns lateral.
	/// No velocity math, no coordinate-space mixing — just a direct position write.
	/// </summary>
	private void EnforceLanePosition(Node3D? seg)
	{
		if (seg == null) { if (_dbgFrame % 60 == 0) GD.Print("[LANE] seg null — skipping enforce"); return; }

		var tf = seg.GlobalTransform;

		var right = tf.Basis.X;
		right.Y = 0f;
		if (right.LengthSquared() < 1e-8f) { GD.Print("[LANE] right vec zero"); return; }
		right = right.Normalized();

		var fwd = tf.Basis.Z;
		fwd.Y = 0f;
		if (fwd.LengthSquared() < 1e-8f) return;
		fwd = fwd.Normalized();

		// Decompose current world position into the segment's axes
		var toPlayer = GlobalPosition - tf.Origin;
		var fwdDist  = toPlayer.Dot(fwd);

		// Reconstruct: keep Y from physics, override lateral to _smoothLaneX
		var corrected = tf.Origin + fwd * fwdDist + right * _smoothLaneX;
		corrected.Y = GlobalPosition.Y;
		GlobalPosition = corrected;
	}

	// ── Input ──────────────────────────────────────────────────────────────

	private void PollInput(bool inTurnZone)
	{
		if (GetTree()?.Paused == true) return;

		var left  = Input.IsPhysicalKeyPressed(Key.Left)  || Input.IsKeyPressed(Key.Left)
				 || Input.IsPhysicalKeyPressed(Key.A)     || Input.IsKeyPressed(Key.A);
		var right = Input.IsPhysicalKeyPressed(Key.Right) || Input.IsKeyPressed(Key.Right)
				 || Input.IsPhysicalKeyPressed(Key.D)     || Input.IsKeyPressed(Key.D);
		var jump  = Input.IsPhysicalKeyPressed(Key.Space) || Input.IsKeyPressed(Key.Space);
		var qKey  = Input.IsPhysicalKeyPressed(Key.Q)     || Input.IsKeyPressed(Key.Q);
		var eKey  = Input.IsPhysicalKeyPressed(Key.E)     || Input.IsKeyPressed(Key.E);

		// Lane change — rising edge only
		if (left  && !_prevLeft)  { _laneIndex = Mathf.Clamp(_laneIndex - 1, 0, Lanes.Length - 1); GD.Print($"[INPUT] LEFT → laneIndex={_laneIndex}"); }
		if (right && !_prevRight) { _laneIndex = Mathf.Clamp(_laneIndex + 1, 0, Lanes.Length - 1); GD.Print($"[INPUT] RIGHT → laneIndex={_laneIndex}"); }

		// Raw key state log on rising edge (confirm Godot sees the key at all)
		if ((left && !_prevLeft) || (right && !_prevRight))
			GD.Print($"[INPUT] raw: left={left} right={right} paused={GetTree()?.Paused}");

		// Track turn — rising edge, only near end of segment
		if (inTurnZone)
		{
			if (qKey && !_prevQ) _pendingTurn = -1;
			if (eKey && !_prevE) _pendingTurn =  1;
		}

		// Jump — rising edge
		_jumpFired = jump && !_prevJump;

		_prevLeft  = left;
		_prevRight = right;
		_prevJump  = jump;
		_prevQ     = qKey;
		_prevE     = eKey;
	}

	// ── Animation ──────────────────────────────────────────────────────────

	private void UpdateFacing()
	{
		var yaw = Mathf.Atan2(RunForward.X, RunForward.Z);
		_meshRoot.Rotation = new Vector3(_meshRoot.Rotation.X, yaw, _meshRoot.Rotation.Z);
	}

	private void UpdateRunAnimation(float dt)
	{
		if (IsOnFloor())
		{
			var spd = Mathf.Clamp(ForwardSpeed / MaxForwardSpeed, 0.35f, 1f);
			_runPhase += dt * 14.0 * spd;
			var s = Mathf.Sin((float)_runPhase);
			var c = Mathf.Cos((float)_runPhase);
			_armL.Rotation  = new Vector3( 0.85f * s, _armL.Rotation.Y,  _armL.Rotation.Z);
			_armR.Rotation  = new Vector3(-0.85f * s, _armR.Rotation.Y,  _armR.Rotation.Z);
			_legL.Rotation  = new Vector3(-0.95f * s, _legL.Rotation.Y,  _legL.Rotation.Z);
			_legR.Rotation  = new Vector3( 0.95f * s, _legR.Rotation.Y,  _legR.Rotation.Z);
			_torso.Rotation = new Vector3(_torso.Rotation.X, _torso.Rotation.Y, 0.06f * s);
			_meshRoot.Position = new Vector3(0f, Mathf.Abs(c) * 0.04f, 0f);
		}
		else
		{
			_armL.Rotation = new Vector3(Mathf.MoveToward(_armL.Rotation.X,  0.35f, dt * 6f), _armL.Rotation.Y, _armL.Rotation.Z);
			_armR.Rotation = new Vector3(Mathf.MoveToward(_armR.Rotation.X, -0.35f, dt * 6f), _armR.Rotation.Y, _armR.Rotation.Z);
			_legL.Rotation = new Vector3(Mathf.MoveToward(_legL.Rotation.X,  0.2f,  dt * 6f), _legL.Rotation.Y, _legL.Rotation.Z);
			_legR.Rotation = new Vector3(Mathf.MoveToward(_legR.Rotation.X, -0.2f,  dt * 6f), _legR.Rotation.Y, _legR.Rotation.Z);
		}
	}

	// ── Hazards ────────────────────────────────────────────────────────────

	private void CheckHazards()
	{
		for (var i = 0; i < GetSlideCollisionCount(); i++)
		{
			var col = GetSlideCollision(i);
			// Check both the collider and its parent — hazard StaticBody3Ds are
			// children of the Content holder node.
			var collider = col.GetCollider();
			if (collider is Node n && (n.IsInGroup("hazard") || (n.GetParent() is Node p && p.IsInGroup("hazard"))))
			{
				Die();
				return;
			}
		}
	}

	public void AddCoin()
	{
		if (!_alive) return;
		_coins++;
		EmitSignal(SignalName.ScoreChanged, _distanceTravelled, _coins);
	}

	public int GetScoreDistanceMeters() => (int)(_distanceTravelled / 3.0);
	public int GetCoins()               => _coins;

	// ── Reset / Death ──────────────────────────────────────────────────────

	public void ResetRunner()
	{
		_alive     = true;
		CollisionLayer = 1;
		_laneIndex    = 1;
		_smoothLaneX  = Lanes[_laneIndex];
		_distanceTravelled = 0;
		_coins        = 0;
		ForwardSpeed  = 12f;
		Velocity      = Vector3.Zero;
		_runPhase     = 0;
		_pendingTurn  = 0;
		SyncBaseline();

		_track ??= GetNodeOrNull<TrackSpawner>(TrackPath);

		GlobalRotation     = Vector3.Zero;
		_meshRoot.Rotation = Vector3.Zero;
		_meshRoot.Scale    = Vector3.One;
		_meshRoot.Position = Vector3.Zero;
		_armL.Rotation     = Vector3.Zero;
		_armR.Rotation     = Vector3.Zero;
		_legL.Rotation     = Vector3.Zero;
		_legR.Rotation     = Vector3.Zero;
		_torso.Rotation    = Vector3.Zero;

		SnapToLaneWorld();
		UpdateRunForward(_track?.GetClosestSegment(GlobalPosition));
	}

	private void SnapToLaneWorld()
	{
		_track ??= GetNodeOrNull<TrackSpawner>(TrackPath);
		var seg = _track?.GetClosestSegment(GlobalPosition);
		if (seg == null)
		{
			GlobalPosition = new Vector3(Lanes[_laneIndex], 0f, 0f);
			return;
		}
		GlobalPosition = seg.GlobalTransform * new Vector3(Lanes[_laneIndex], 0f, -5f);
	}

	private void SyncBaseline()
	{
		_prevLeft  = Input.IsPhysicalKeyPressed(Key.Left)  || Input.IsKeyPressed(Key.Left)
				  || Input.IsPhysicalKeyPressed(Key.A)     || Input.IsKeyPressed(Key.A);
		_prevRight = Input.IsPhysicalKeyPressed(Key.Right) || Input.IsKeyPressed(Key.Right)
				  || Input.IsPhysicalKeyPressed(Key.D)     || Input.IsKeyPressed(Key.D);
		_prevJump  = Input.IsPhysicalKeyPressed(Key.Space) || Input.IsKeyPressed(Key.Space);
		_prevQ     = Input.IsPhysicalKeyPressed(Key.Q)     || Input.IsKeyPressed(Key.Q);
		_prevE     = Input.IsPhysicalKeyPressed(Key.E)     || Input.IsKeyPressed(Key.E);
		_jumpFired = false;
	}

	private void Die()
	{
		if (!_alive) return;
		_alive = false;
		SyncBaseline();
		Velocity = Vector3.Zero;
		CollisionLayer = 0;
		_meshRoot.Rotation = new Vector3(Mathf.DegToRad(-38f), _meshRoot.Rotation.Y, _meshRoot.Rotation.Z);
		EmitSignal(SignalName.Died);
	}
}
