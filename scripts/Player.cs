using Godot;

namespace TempleRun;

public partial class Player : CharacterBody3D
{
	[Signal]
	public delegate void DiedEventHandler();

	[Signal]
	public delegate void ScoreChangedEventHandler(double distance, int coins);

	public static readonly float[] Lanes = { -2.2f, 0f, 2.2f };
	private const float LaneChangeSpeed = 14f;
	private const float Gravity = 28f;
	private const float JumpVelocity = 10.5f;

	private static readonly StringName LaneLeft = "lane_left";
	private static readonly StringName LaneRight = "lane_right";
	private static readonly StringName RunnerJump = "runner_jump";
	private static readonly StringName TurnLeft = "turn_left";
	private static readonly StringName TurnRight = "turn_right";

	[Export] public float ForwardSpeed { get; set; } = 12f;
	[Export] public float SpeedRampPerSec { get; set; } = 0.08f;
	[Export] public float MaxForwardSpeed { get; set; } = 26f;
	[Export] public NodePath TrackPath { get; set; } = new();

	public Vector3 RunForward { get; private set; } = new(0f, 0f, 1f);

	public bool IsAlive => _alive;

	private TrackSpawner? _track;
	private int _laneIndex = 1;
	private bool _alive = true;
	private double _distanceTravelled;
	private int _coins;
	private double _runPhase;

	private int _pendingTurn;

	// Rising-edge state for lane keys
	private bool _prevLaneLeft;
	private bool _prevLaneRight;

	// Rising-edge state for turn / jump keys
	private bool _prevTurnQ;
	private bool _prevTurnE;
	private bool _prevJumpSpace;

	private Node3D _meshRoot = null!;
	private Node3D _armL = null!;
	private Node3D _armR = null!;
	private Node3D _legL = null!;
	private Node3D _legR = null!;
	private Node3D _torso = null!;

	public override void _Ready()
	{
		_meshRoot = GetNode<Node3D>("MeshRoot");
		_armL = GetNode<Node3D>("MeshRoot/LeftArmPivot");
		_armR = GetNode<Node3D>("MeshRoot/RightArmPivot");
		_legL = GetNode<Node3D>("MeshRoot/LeftLegPivot");
		_legR = GetNode<Node3D>("MeshRoot/RightLegPivot");
		_torso = GetNode<Node3D>("MeshRoot/Torso");

		if (!TrackPath.IsEmpty)
			_track = GetNodeOrNull<TrackSpawner>(TrackPath);

		EnsureActions();
		SnapToLaneWorld();
		SyncBaseline();
	}

	public void ConfigureForNewRun()
	{
		_track ??= GetNodeOrNull<TrackSpawner>(TrackPath);
	}

	private void EnsureActions()
	{
		BindKey(LaneLeft, Key.A);
		BindKey(LaneLeft, Key.Left);
		BindKey(LaneRight, Key.D);
		BindKey(LaneRight, Key.Right);
		BindKey(RunnerJump, Key.Space);
		BindKey(TurnLeft, Key.Q);
		BindKey(TurnRight, Key.E);
	}

	private static void BindKey(StringName action, Key code)
	{
		if (!InputMap.HasAction(action))
			InputMap.AddAction(action);

		foreach (var ev in InputMap.ActionGetEvents(action))
		{
			if (ev is InputEventKey k && k.PhysicalKeycode == code)
				return;
		}

		var key = new InputEventKey { PhysicalKeycode = code };
		InputMap.ActionAddEvent(action, key);
	}

	public int PullTurnRequest()
	{
		if (_pendingTurn == 0)
			return 0;
		var v = _pendingTurn;
		_pendingTurn = 0;
		return v;
	}

	/// <summary>Kept for compatibility with Game.cs — actual lane input is polled in _PhysicsProcess.</summary>
	public void NotifyLaneStep(int delta) { }

	public static bool IsLaneLeftKeyEvent(InputEventKey k)
	{
		return k.Keycode == Key.Left || k.Keycode == Key.A
			|| k.PhysicalKeycode == Key.Left || k.PhysicalKeycode == Key.A;
	}

	public static bool IsLaneRightKeyEvent(InputEventKey k)
	{
		return k.Keycode == Key.Right || k.Keycode == Key.D
			|| k.PhysicalKeycode == Key.Right || k.PhysicalKeycode == Key.D;
	}

	public override void _PhysicsProcess(double delta)
	{
		var dt = (float)delta;
		if (!_alive)
			return;

		UpdateRunForwardFromTrack();

		var turnZone = UpdateTurnZone();
		PollLaneKeys();
		PollTurnAndJumpKeys(turnZone);

		ForwardSpeed = Mathf.Min(MaxForwardSpeed, ForwardSpeed + SpeedRampPerSec * dt);
		var vy = Velocity.Y;
		var spaceDown = Input.IsPhysicalKeyPressed(Key.Space) || Input.IsKeyPressed(Key.Space);
		if (!IsOnFloor())
			vy -= Gravity * dt;
		else
		{
			vy = 0f;
			if (spaceDown && !_prevJumpSpace)
			{
				vy = JumpVelocity;
				_meshRoot.Scale = new Vector3(1f, 1.08f, 1f);
			}
		}

		_prevJumpSpace = spaceDown;

		var prevY = GlobalPosition.Y;
		var forwardVel = RunForward * ForwardSpeed;
		var lateralVel = ComputeLaneLateralVelocity();
		Velocity = new Vector3(forwardVel.X + lateralVel.X, vy, forwardVel.Z + lateralVel.Z);
		MoveAndSlide();

		CheckHazards();

		// Fall-death
		if (GlobalPosition.Y < -8f)
		{
			Die();
			return;
		}

		if (IsOnFloor() && prevY > 0.35f)
			_meshRoot.Scale = Vector3.One;

		UpdateRunAnimation(dt);
		UpdateFacing();

		_distanceTravelled += ForwardSpeed * dt;
		EmitSignal(SignalName.ScoreChanged, _distanceTravelled, _coins);
	}

	// ── Input ──────────────────────────────────────────────────────────────

	/// <summary>
	/// Rising-edge poll for lane keys — runs every physics frame, independent of the
	/// GUI event chain, so keys can never be eaten by CanvasLayer / HUD focus.
	/// Directly mutates _laneIndex so there is no buffer step that can be skipped.
	/// </summary>
	private void PollLaneKeys()
	{
		if (!_alive) return;
		if (GetTree()?.Paused == true) return;

		var leftDown = Input.IsPhysicalKeyPressed(Key.Left) || Input.IsKeyPressed(Key.Left)
			|| Input.IsPhysicalKeyPressed(Key.A) || Input.IsKeyPressed(Key.A);
		var rightDown = Input.IsPhysicalKeyPressed(Key.Right) || Input.IsKeyPressed(Key.Right)
			|| Input.IsPhysicalKeyPressed(Key.D) || Input.IsKeyPressed(Key.D);

		if (leftDown && !_prevLaneLeft)
			_laneIndex = Mathf.Clamp(_laneIndex - 1, 0, Lanes.Length - 1);
		if (rightDown && !_prevLaneRight)
			_laneIndex = Mathf.Clamp(_laneIndex + 1, 0, Lanes.Length - 1);

		_prevLaneLeft = leftDown;
		_prevLaneRight = rightDown;
	}

	private void PollTurnAndJumpKeys(bool turnZone)
	{
		if (GetTree()?.Paused == true)
			return;

		var qDown = Input.IsPhysicalKeyPressed(Key.Q) || Input.IsKeyPressed(Key.Q);
		var eDown = Input.IsPhysicalKeyPressed(Key.E) || Input.IsKeyPressed(Key.E);

		if (turnZone)
		{
			if (qDown && !_prevTurnQ)
				_pendingTurn = -1;
			else if (eDown && !_prevTurnE)
				_pendingTurn = 1;
		}

		_prevTurnQ = qDown;
		_prevTurnE = eDown;
	}

	// ── Movement ───────────────────────────────────────────────────────────

	private void UpdateRunForwardFromTrack()
	{
		var seg = _track?.GetClosestSegment(GlobalPosition);
		if (seg == null)
			return;

		var f = seg.GlobalTransform.Basis.Z;
		f.Y = 0f;
		if (f.LengthSquared() > 1e-6f)
			RunForward = f.Normalized();
	}

	private bool UpdateTurnZone()
	{
		var seg = _track?.GetClosestSegment(GlobalPosition);
		if (seg == null)
			return false;

		var local = seg.GlobalTransform.AffineInverse() * GlobalPosition;
		return local.Z > 2.8f;
	}

	/// <summary>
	/// Computes the lateral velocity component needed to reach the target lane.
	/// IMPORTANT: the local-space target uses lp.Y (not GlobalPosition.Y) so the
	/// world-space transform gives the correct position on the track surface.
	/// </summary>
	private Vector3 ComputeLaneLateralVelocity()
	{
		var seg = _track?.GetClosestSegment(GlobalPosition);
		if (seg == null)
			return Vector3.Zero;

		var inv = seg.GlobalTransform.AffineInverse();
		var lp = inv * GlobalPosition;

		// Use lp.Y (local-space height), NOT GlobalPosition.Y (world-space) —
		// mixing coordinate spaces here was the root cause of the lane bug.
		var target = seg.GlobalTransform * new Vector3(Lanes[_laneIndex], lp.Y, lp.Z);

		var right = seg.GlobalTransform.Basis.X;
		right.Y = 0f;
		if (right.LengthSquared() < 1e-8f)
			return Vector3.Zero;
		right = right.Normalized();

		var to = target - GlobalPosition;
		to.Y = 0f;
		var lateralErr = to.Dot(right);
		if (Mathf.Abs(lateralErr) < 0.02f)
			return Vector3.Zero;

		var lateralSpeed = Mathf.Clamp(lateralErr * 18f, -LaneChangeSpeed, LaneChangeSpeed);
		return right * lateralSpeed;
	}

	// ── Animation & visuals ────────────────────────────────────────────────

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
			_armL.Rotation = new Vector3(0.85f * s, _armL.Rotation.Y, _armL.Rotation.Z);
			_armR.Rotation = new Vector3(-0.85f * s, _armR.Rotation.Y, _armR.Rotation.Z);
			_legL.Rotation = new Vector3(-0.95f * s, _legL.Rotation.Y, _legL.Rotation.Z);
			_legR.Rotation = new Vector3(0.95f * s, _legR.Rotation.Y, _legR.Rotation.Z);
			_torso.Rotation = new Vector3(_torso.Rotation.X, _torso.Rotation.Y, 0.06f * s);
			_meshRoot.Position = new Vector3(0f, Mathf.Abs(c) * 0.04f, 0f);
		}
		else
		{
			_armL.Rotation = new Vector3(
				Mathf.MoveToward(_armL.Rotation.X, 0.35f, dt * 6f), _armL.Rotation.Y, _armL.Rotation.Z);
			_armR.Rotation = new Vector3(
				Mathf.MoveToward(_armR.Rotation.X, -0.35f, dt * 6f), _armR.Rotation.Y, _armR.Rotation.Z);
			_legL.Rotation = new Vector3(
				Mathf.MoveToward(_legL.Rotation.X, 0.2f, dt * 6f), _legL.Rotation.Y, _legL.Rotation.Z);
			_legR.Rotation = new Vector3(
				Mathf.MoveToward(_legR.Rotation.X, -0.2f, dt * 6f), _legR.Rotation.Y, _legR.Rotation.Z);
		}
	}

	// ── Hazards / death ────────────────────────────────────────────────────

	private void CheckHazards()
	{
		for (var i = 0; i < GetSlideCollisionCount(); i++)
		{
			var col = GetSlideCollision(i);
			if (col.GetNormal().Y < 0.25f)
				continue;
			var c = col.GetCollider();
			if (c is Node n && n.IsInGroup("hazard"))
			{
				Die();
				return;
			}
		}
	}

	// ── Reset ──────────────────────────────────────────────────────────────

	public void ResetRunner()
	{
		_alive = true;
		CollisionLayer = 1;
		_laneIndex = 1;
		_distanceTravelled = 0;
		_coins = 0;
		ForwardSpeed = 12f;
		Velocity = Vector3.Zero;
		_runPhase = 0;
		_pendingTurn = 0;
		SyncBaseline();

		_track ??= GetNodeOrNull<TrackSpawner>(TrackPath);

		GlobalRotation = Vector3.Zero;
		_meshRoot.Rotation = Vector3.Zero;
		_meshRoot.Scale = Vector3.One;
		_meshRoot.Position = Vector3.Zero;
		_armL.Rotation = Vector3.Zero;
		_armR.Rotation = Vector3.Zero;
		_legL.Rotation = Vector3.Zero;
		_legR.Rotation = Vector3.Zero;
		_torso.Rotation = Vector3.Zero;

		SnapToLaneWorld();
		UpdateRunForwardFromTrack();
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

	public void AddCoin()
	{
		if (!_alive)
			return;
		_coins++;
		EmitSignal(SignalName.ScoreChanged, _distanceTravelled, _coins);
	}

	public int GetScoreDistanceMeters() => (int)(_distanceTravelled / 3.0);

	public int GetCoins() => _coins;

	/// <summary>Snapshots current key states so rising-edge detection doesn't false-fire on the first frame.</summary>
	private void SyncBaseline()
	{
		_prevTurnQ = Input.IsPhysicalKeyPressed(Key.Q) || Input.IsKeyPressed(Key.Q);
		_prevTurnE = Input.IsPhysicalKeyPressed(Key.E) || Input.IsKeyPressed(Key.E);
		_prevJumpSpace = Input.IsPhysicalKeyPressed(Key.Space) || Input.IsKeyPressed(Key.Space);
		_prevLaneLeft = Input.IsPhysicalKeyPressed(Key.Left) || Input.IsKeyPressed(Key.Left)
			|| Input.IsPhysicalKeyPressed(Key.A) || Input.IsKeyPressed(Key.A);
		_prevLaneRight = Input.IsPhysicalKeyPressed(Key.Right) || Input.IsKeyPressed(Key.Right)
			|| Input.IsPhysicalKeyPressed(Key.D) || Input.IsKeyPressed(Key.D);
	}

	private void Die()
	{
		if (!_alive)
			return;
		_alive = false;
		SyncBaseline();
		Velocity = Vector3.Zero;
		CollisionLayer = 0;
		_meshRoot.Rotation = new Vector3(Mathf.DegToRad(-38f), _meshRoot.Rotation.Y, _meshRoot.Rotation.Z);
		EmitSignal(SignalName.Died);
	}
}
