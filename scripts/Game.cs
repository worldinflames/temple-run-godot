using Godot;

namespace TempleRun;

public partial class Game : Node3D
{
	private Player _player = null!;
	private Node3D _camPivot = null!;
	private TrackSpawner _track = null!;
	private GameHud _hud = null!;

	private bool _alive = true;
	private Vector3 _camSmoothed;

	public override void _Ready()
	{
		_player = GetNode<Player>("Player");
		_camPivot = GetNode<Node3D>("CameraPivot");
		_track = GetNode<TrackSpawner>("TrackSpawner");
		_hud = GetNode<GameHud>("HUD");

		_player.ConfigureForNewRun();
		_track.ResetTrack();
		_player.ResetRunner();

		_hud.Setup(this);
		_hud.SetPlayingStart();

		_player.Died += OnPlayerDied;
		_player.ScoreChanged += OnScoreChanged;

		_camSmoothed = CameraTargetPosition();
		_camPivot.GlobalPosition = _camSmoothed;
	}

	/// <summary>
	/// Wywoływane od korzenia w dół — przed CanvasLayer/HUD, więc strzałki nie giną w GUI.
	/// + CharacterBody3D: ruch między pasami musi iść w Velocity, nie w GlobalPosition po MoveAndSlide.
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		if (!_alive)
			return;
		if (GetTree().Paused)
			return;
		if (@event is not InputEventKey k || !k.Pressed || k.Echo)
			return;

		if (Player.IsLaneLeftKeyEvent(k))
		{
			_player.NotifyLaneStep(-1);
			GetViewport().SetInputAsHandled();
		}
		else if (Player.IsLaneRightKeyEvent(k))
		{
			_player.NotifyLaneStep(1);
			GetViewport().SetInputAsHandled();
		}
	}

	public bool CanPause() => _alive;

	public void TogglePause()
	{
		if (!_alive)
			return;
		var p = !GetTree().Paused;
		GetTree().Paused = p;
		_hud.SetPaused(p);
	}

	public void ForceUnpause()
	{
		GetTree().Paused = false;
		_hud.SetPaused(false);
	}

	public void RestartRun()
	{
		_alive = true;
		ForceUnpause();
		_track.ResetTrack();
		_player.ResetRunner();
		_hud.SetPlayingStart();
		_camSmoothed = CameraTargetPosition();
		_camPivot.GlobalPosition = _camSmoothed;
	}

	public override void _PhysicsProcess(double delta)
	{
		if (GetTree().Paused)
			return;

		var dt = (float)delta;
		var target = CameraTargetPosition();
		_camSmoothed = _camSmoothed.Lerp(target, 1f - Mathf.Exp(-5f * dt));
		_camPivot.GlobalPosition = _camSmoothed;
		var f = _player.RunForward;
		f.Y = 0f;
		if (f.LengthSquared() > 1e-6f)
			f = f.Normalized();
		else
			f = new Vector3(0f, 0f, 1f);
		_camPivot.LookAt(_player.GlobalPosition + f * 2f + new Vector3(0f, 1f, 0f), Vector3.Up);
	}

	private void OnPlayerDied()
	{
		_alive = false;
		ForceUnpause();
		_hud.ShowGameOver(_player.GetScoreDistanceMeters(), _player.GetCoins());
	}

	private void OnScoreChanged(double dist, int coins)
	{
		_hud.UpdateHud(dist, coins);
	}

	private Vector3 CameraTargetPosition()
	{
		var f = _player.RunForward;
		f.Y = 0f;
		if (f.LengthSquared() > 1e-6f)
			f = f.Normalized();
		else
			f = new Vector3(0f, 0f, 1f);
		return _player.GlobalPosition - f * 7.5f + new Vector3(0f, 3.2f, 0f);
	}
}
