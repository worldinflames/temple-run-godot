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

	// Decoration materials (created in code)
	private StandardMaterial3D? _matTreeTrunk;
	private StandardMaterial3D? _matTreeFoliage;
	private StandardMaterial3D? _matCastleStone;
	private StandardMaterial3D? _matMoat;
	private StandardMaterial3D? _matTorchFire;
	private StandardMaterial3D? _matRoof;

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

		_matTreeTrunk = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.18f, 0.07f) };
		_matTreeFoliage = new StandardMaterial3D { AlbedoColor = new Color(0.10f, 0.42f, 0.13f) };
		_matCastleStone = new StandardMaterial3D { AlbedoColor = new Color(0.60f, 0.58f, 0.52f) };
		_matMoat = new StandardMaterial3D { AlbedoColor = new Color(0.04f, 0.16f, 0.36f), Roughness = 0.08f };
		_matTorchFire = new StandardMaterial3D
		{
			AlbedoColor = new Color(1.0f, 0.55f, 0.05f),
			EmissionEnabled = true,
			Emission = new Color(1.0f, 0.40f, 0.0f),
			EmissionEnergyMultiplier = 2.5f
		};
		_matRoof = new StandardMaterial3D { AlbedoColor = new Color(0.60f, 0.10f, 0.10f) };

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

	/// <summary>Called by Game._Ready() to directly wire the player reference (NodePath resolution unreliable).</summary>
	public void SetPlayer(Player player) => _player = player;

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

		var right = PathForward.Cross(Vector3.Up).Normalized();
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
		var right = pf.Cross(Vector3.Up).Normalized();
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

		// Side decorations always present regardless of obstacle type
		SpawnSideDecorations(holder, seq);

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

	// ─── Side Decoration Methods ──────────────────────────────────────────────

	private void SpawnSideDecorations(Node3D holder, int seq)
	{
		var theme = _rng.RandiRange(0, 2);
		switch (theme)
		{
			case 0: // Dense forest both sides
				SpawnForestSide(holder, -1f);
				SpawnForestSide(holder, 1f);
				break;
			case 1: // Castle walls both sides
				SpawnCastleSide(holder, -1f);
				SpawnCastleSide(holder, 1f);
				break;
			default: // Mixed: forest left, castle right
				SpawnForestSide(holder, -1f);
				SpawnCastleSide(holder, 1f);
				break;
		}
	}

	private void SpawnForestSide(Node3D holder, float side)
	{
		var halfLen = SegmentLength * 0.5f;
		var count = _rng.RandiRange(3, 6);
		for (var i = 0; i < count; i++)
		{
			var x = side * _rng.RandfRange(4.5f, 9.0f);
			var z = _rng.RandfRange(-halfLen + 1f, halfLen - 1f);
			var treeH = _rng.RandfRange(4.0f, 8.0f);
			var trunkR = _rng.RandfRange(0.18f, 0.35f);

			// Trunk
			var trunk = new MeshInstance3D();
			trunk.Mesh = new CylinderMesh { TopRadius = trunkR * 0.55f, BottomRadius = trunkR, Height = treeH * 0.5f };
			trunk.Position = new Vector3(x, treeH * 0.25f, z);
			if (_matTreeTrunk != null) trunk.SetSurfaceOverrideMaterial(0, _matTreeTrunk);
			holder.AddChild(trunk);

			// Lower foliage ball
			var fr = _rng.RandfRange(1.2f, 2.2f);
			var fol1 = new MeshInstance3D();
			fol1.Mesh = new SphereMesh { Radius = fr, Height = fr * 2f };
			fol1.Position = new Vector3(x, treeH * 0.5f + fr * 0.5f, z);
			if (_matTreeFoliage != null) fol1.SetSurfaceOverrideMaterial(0, _matTreeFoliage);
			holder.AddChild(fol1);

			// Upper foliage ball
			var fol2 = new MeshInstance3D();
			fol2.Mesh = new SphereMesh { Radius = fr * 0.65f, Height = fr * 1.3f };
			fol2.Position = new Vector3(x, treeH * 0.5f + fr * 1.6f, z);
			if (_matTreeFoliage != null) fol2.SetSurfaceOverrideMaterial(0, _matTreeFoliage);
			holder.AddChild(fol2);
		}
	}

	private void SpawnCastleSide(Node3D holder, float side)
	{
		var wallX = side * 5.2f;
		var halfLen = SegmentLength * 0.5f;

		// Moat (dark water strip between track and wall)
		var moat = new MeshInstance3D();
		moat.Mesh = new BoxMesh { Size = new Vector3(1.8f, 0.06f, SegmentLength) };
		moat.Position = new Vector3(side * 3.8f, 0.01f, 0f);
		if (_matMoat != null) moat.SetSurfaceOverrideMaterial(0, _matMoat);
		holder.AddChild(moat);

		// Main wall
		var wall = new MeshInstance3D();
		wall.Mesh = new BoxMesh { Size = new Vector3(1.3f, 4.5f, SegmentLength - 0.5f) };
		wall.Position = new Vector3(wallX, 2.25f, 0f);
		if (_matCastleStone != null) wall.SetSurfaceOverrideMaterial(0, _matCastleStone);
		holder.AddChild(wall);

		// Battlements (alternating merlons on top)
		var merCount = (int)(SegmentLength / 2.2f);
		for (var i = 0; i < merCount; i++)
		{
			if (i % 2 != 0) continue;
			var mz = -halfLen + 1.3f + i * 2.2f;
			var merlon = new MeshInstance3D();
			merlon.Mesh = new BoxMesh { Size = new Vector3(1.3f, 1.1f, 0.9f) };
			merlon.Position = new Vector3(wallX, 4.5f + 0.55f, mz);
			if (_matCastleStone != null) merlon.SetSurfaceOverrideMaterial(0, _matCastleStone);
			holder.AddChild(merlon);
		}

		// Tower at one end of the segment
		var towerZ = _rng.Randf() > 0.5f ? halfLen - 2.0f : -halfLen + 2.0f;
		SpawnTower(holder, wallX, towerZ, side);

		// Occasionally a second tower at the other end
		if (_rng.Randf() > 0.55f)
			SpawnTower(holder, wallX, -towerZ, side);
	}

	private void SpawnTower(Node3D holder, float x, float z, float side)
	{
		const float tw = 2.2f;
		const float th = 8.0f;

		// Tower body
		var body = new MeshInstance3D();
		body.Mesh = new BoxMesh { Size = new Vector3(tw, th, tw) };
		body.Position = new Vector3(x, th * 0.5f, z);
		if (_matCastleStone != null) body.SetSurfaceOverrideMaterial(0, _matCastleStone);
		holder.AddChild(body);

		// Corner merlons on tower top
		var corners = new[] { new Vector2(-0.7f, -0.7f), new Vector2(0.7f, -0.7f), new Vector2(-0.7f, 0.7f), new Vector2(0.7f, 0.7f) };
		foreach (var c in corners)
		{
			var m = new MeshInstance3D();
			m.Mesh = new BoxMesh { Size = new Vector3(0.55f, 0.9f, 0.55f) };
			m.Position = new Vector3(x + c.X, th + 0.45f, z + c.Y);
			if (_matCastleStone != null) m.SetSurfaceOverrideMaterial(0, _matCastleStone);
			holder.AddChild(m);
		}

		// Conical roof
		var roof = new MeshInstance3D();
		roof.Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = tw * 0.8f, Height = th * 0.4f };
		roof.Position = new Vector3(x, th + th * 0.2f, z);
		if (_matRoof != null) roof.SetSurfaceOverrideMaterial(0, _matRoof);
		holder.AddChild(roof);

		// Torch on the wall-facing side of the tower
		var torchX = x + side * -1.2f;
		var torchBody = new MeshInstance3D();
		torchBody.Mesh = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.09f, Height = 0.45f };
		torchBody.Position = new Vector3(torchX, 5.2f, z);
		if (_matCastleStone != null) torchBody.SetSurfaceOverrideMaterial(0, _matCastleStone);
		holder.AddChild(torchBody);

		var flame = new MeshInstance3D();
		flame.Mesh = new SphereMesh { Radius = 0.17f, Height = 0.34f };
		flame.Position = new Vector3(torchX, 5.68f, z);
		if (_matTorchFire != null) flame.SetSurfaceOverrideMaterial(0, _matTorchFire);
		holder.AddChild(flame);
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
