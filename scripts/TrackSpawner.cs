using Godot;

namespace TempleRun;

/// <summary>
/// Procedural track with 90° turns; segments recycle ahead of the player.
/// </summary>
public partial class TrackSpawner : Node3D
{
	public const float SegmentLength = 14f;
	public static readonly float[] Lanes = { -2.2f, 0f, 2.2f };
	private const int SegmentPool = 12;

	[Export] public NodePath PlayerPath { get; set; } = new();

	/// <summary>World +Z axis of the current path — movement direction.</summary>
	public Vector3 PathForward { get; private set; } = new(0f, 0f, 1f);

	private Player? _player;
	private readonly List<SegmentSlot> _slots = new();
	private readonly RandomNumberGenerator _rng = new();

	private Material? _matGround;
	private Material? _matLane;
	private Material? _matStone;
	private Material? _matSpike;
	private Material? _matCoin;

	private Vector3 _nextCenter;
	private int _segmentsSpawned;

	private sealed class SegmentSlot
	{
		public required Node3D Node { get; init; }
	}

	public override void _Ready()
	{
		_rng.Randomize();
		_matGround = GD.Load<Material>("res://assets/materials/ground.tres");
		_matLane = GD.Load<Material>("res://assets/materials/lane_mark.tres");
		_matStone = GD.Load<Material>("res://assets/materials/obstacle_stone.tres");
		_matSpike = GD.Load<Material>("res://assets/materials/obstacle_spike.tres");
		_matCoin = GD.Load<Material>("res://assets/materials/coin_gold.tres");

		if (!PlayerPath.IsEmpty)
			_player = GetNodeOrNull<Player>(PlayerPath);

		for (var i = 0; i < SegmentPool; i++)
		{
			var root = new Node3D { Name = $"Seg_{i}" };
			AddChild(root);
			BuildSegmentVisuals(root);
			_slots.Add(new SegmentSlot { Node = root });
		}

		ResetTrack();
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_player == null || !_player.IsAlive)
		{
			if (_player == null) GD.Print("[TRACK] _player is NULL");
			return;
		}

		// Kierunek „przód” dla recyklingu = kierunek biegu gracza (środek odcinka pod stopami),
		// nie PathForward końcówki — inaczej segmenty nie są uznawane za „z tyłu” i powstaje dziura.
		// Zawsze przenoś najpierw segment najbardziej w tyle.
		while (true)
		{
			var pf = _player.RunForward;
			pf.Y = 0f;
			if (pf.LengthSquared() < 1e-6f)
			{
				pf = PathForward;
				pf.Y = 0f;
			}

			if (pf.LengthSquared() < 1e-6f)
				pf = new Vector3(0f, 0f, 1f);
			pf = pf.Normalized();

			Node3D? best = null;
			var bestDot = float.MinValue;
			foreach (var slot in _slots)
			{
				var root = slot.Node;
				var toPlayer = _player.GlobalPosition - root.GlobalPosition;
				toPlayer.Y = 0f;
				var d = toPlayer.Dot(pf);
				// d > 0: segment centre is behind the player; recycle the farthest one first.
				if (d > SegmentLength * 0.75f && d > bestDot)
				{
					bestDot = d;
					best = root;
				}
			}

			if (best == null)
				break;
			GD.Print($"[TRACK] Recycle Z={best.GlobalPosition.Z:F0}->nextCenter={_nextCenter.Z:F0} playerZ={_player.GlobalPosition.Z:F1} pf={pf}");
			RecycleSegment(best);
		}
	}

	public void ResetTrack()
	{
		PathForward = new Vector3(0f, 0f, 1f);
		_segmentsSpawned = 0;

		for (var i = 0; i < _slots.Count; i++)
			PlaceSegmentAtIndex(_slots[i].Node, i);

		_nextCenter = PathForward * (SegmentLength * _slots.Count);

		for (var i = 0; i < _slots.Count; i++)
			RandomizeSegmentContent(_slots[i].Node, i);
	}

	public Node3D? GetClosestSegment(Vector3 worldPos)
	{
		Node3D? best = null;
		var bestD = float.MaxValue;
		foreach (var slot in _slots)
		{
			var p = slot.Node.GlobalPosition;
			var d = (p - worldPos);
			d.Y = 0f;
			var l = d.LengthSquared();
			if (l < bestD)
			{
				bestD = l;
				best = slot.Node;
			}
		}

		return best;
	}

	private void RecycleSegment(Node3D root)
	{
		var turn = _player?.PullTurnRequest() ?? 0;
		if (turn != 0)
			PathForward = PathForward.Rotated(Vector3.Up, Mathf.Pi / 2f * turn).Normalized();

		var right = Vector3.Up.Cross(PathForward).Normalized();
		var basis = new Basis(right, Vector3.Up, PathForward);
		root.GlobalTransform = new Transform3D(basis, _nextCenter);
		_nextCenter += PathForward * SegmentLength;

		_segmentsSpawned++;
		RandomizeSegmentContent(root, _segmentsSpawned);
	}

	private void PlaceSegmentAtIndex(Node3D root, int index)
	{
		var pf = new Vector3(0f, 0f, 1f);
		var center = pf * (SegmentLength * index);
		var right = Vector3.Up.Cross(pf).Normalized();
		var basis = new Basis(right, Vector3.Up, pf);
		root.GlobalTransform = new Transform3D(basis, center);
	}

	private void BuildSegmentVisuals(Node3D root)
	{
		var floorBody = new StaticBody3D
		{
			CollisionLayer = 2,
			CollisionMask = 0
		};
		floorBody.AddToGroup("track_floor");
		root.AddChild(floorBody);

		var floorMesh = new MeshInstance3D();
		var box = new BoxMesh { Size = new Vector3(6.5f, 0.45f, SegmentLength) };
		floorMesh.Mesh = box;
		floorMesh.Position = new Vector3(0f, -0.225f, 0f);
		if (_matGround != null)
			floorMesh.SetSurfaceOverrideMaterial(0, _matGround);
		floorBody.AddChild(floorMesh);

		var floorShape = new CollisionShape3D
		{
			Shape = new BoxShape3D { Size = box.Size },
			Position = floorMesh.Position
		};
		floorBody.AddChild(floorShape);

		foreach (var lx in Lanes)
		{
			var stripe = new MeshInstance3D();
			var sm = new BoxMesh { Size = new Vector3(0.08f, 0.02f, SegmentLength * 0.92f) };
			stripe.Mesh = sm;
			stripe.Position = new Vector3(lx, 0.02f, 0f);
			if (_matLane != null)
				stripe.SetSurfaceOverrideMaterial(0, _matLane);
			root.AddChild(stripe);
		}

		var holder = new Node3D { Name = "Content" };
		root.AddChild(holder);
	}

	private void RandomizeSegmentContent(Node3D root, int seq)
	{
		var holder = root.GetNode<Node3D>("Content");
		foreach (var c in holder.GetChildren())
			c.QueueFree();

		var roll = (float)_rng.Randf();
		var early = seq < 3;
		if (early)
		{
			if (roll < 0.35f)
				return;
			SpawnCoinArc(holder, Lanes[_rng.RandiRange(0, Lanes.Length - 1)]);
			return;
		}

		if (roll < 0.18f)
			return;

		var lane = _rng.RandiRange(0, Lanes.Length - 1);
		var lx = Lanes[lane];

		if (roll < 0.52f)
			SpawnWallObstacle(holder, lx);
		else if (roll < 0.78f)
			SpawnSpikeRow(holder, lx);
		else
			SpawnCoinArc(holder, lx);
	}

	private void SpawnWallObstacle(Node3D holder, float lx)
	{
		var body = new StaticBody3D
		{
			CollisionLayer = 8,
			CollisionMask = 0
		};
		body.AddToGroup("hazard");

		var meshI = new MeshInstance3D();
		var msh = new BoxMesh { Size = new Vector3(1.6f, 2.2f, 1f) };
		meshI.Mesh = msh;
		meshI.Position = new Vector3(lx, 1.1f, _rng.RandfRange(-2f, 2f));
		if (_matStone != null)
			meshI.SetSurfaceOverrideMaterial(0, _matStone);
		body.AddChild(meshI);

		var col = new CollisionShape3D
		{
			Shape = new BoxShape3D { Size = msh.Size },
			Position = meshI.Position
		};
		body.AddChild(col);
		holder.AddChild(body);
	}

	private void SpawnSpikeRow(Node3D holder, float centerLaneX)
	{
		for (var i = 0; i < 3; i++)
		{
			var x = centerLaneX + (i - 1) * 0.55f;
			var body = new StaticBody3D
			{
				CollisionLayer = 8,
				CollisionMask = 0
			};
			body.AddToGroup("hazard");

			var meshI = new MeshInstance3D();
			var cone = new CylinderMesh
			{
				TopRadius = 0.02f,
				BottomRadius = 0.45f,
				Height = 0.9f
			};
			meshI.Mesh = cone;
			meshI.Position = new Vector3(x, 0.45f, _rng.RandfRange(-3f, 3f));
			if (_matSpike != null)
				meshI.SetSurfaceOverrideMaterial(0, _matSpike);
			body.AddChild(meshI);

			var col = new CollisionShape3D
			{
				Shape = new CapsuleShape3D { Radius = 0.35f, Height = 0.95f },
				Position = meshI.Position + new Vector3(0f, 0.05f, 0f)
			};
			body.AddChild(col);
			holder.AddChild(body);
		}
	}

	private void SpawnCoinArc(Node3D holder, float lx)
	{
		var baseZ = _rng.RandfRange(-4f, 4f);
		for (var i = 0; i < 5; i++)
		{
			var area = new Area3D
			{
				CollisionLayer = 0,
				CollisionMask = 1,
				Monitoring = true,
				Position = new Vector3(lx, 1.05f + Mathf.Sin(i * 0.65f) * 0.35f, baseZ + i * 1.1f)
			};
			area.AddToGroup("coin");

			var meshI = new MeshInstance3D();
			var ball = new SphereMesh { Radius = 0.38f, Height = 0.76f };
			meshI.Mesh = ball;
			if (_matCoin != null)
				meshI.SetSurfaceOverrideMaterial(0, _matCoin);
			area.AddChild(meshI);

			var col = new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.45f } };
			area.AddChild(col);

			area.BodyEntered += body => OnCoinBodyEntered(body, area);
			holder.AddChild(area);
		}
	}

	private void OnCoinBodyEntered(Node3D body, Area3D coin)
	{
		if (!body.IsInGroup("runner"))
			return;
		if (body is Player p)
			p.AddCoin();
		coin.QueueFree();
	}
}
