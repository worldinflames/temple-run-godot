using Godot;

namespace TempleRun;

/// <summary>Interfejs gry w czasie rozgrywki, pauza i ekran końca z zapisem wyniku.</summary>
public partial class GameHud : CanvasLayer
{
	private Label _dist = null!;
	private Label _coins = null!;
	private Label _help = null!;
	private ColorRect _goOverlay = null!;
	private Label _goTitle = null!;
	private Label _goSubtitle = null!;
	private Label _nameHint = null!;
	private LineEdit _nameEdit = null!;
	private Button _saveBtn = null!;
	private Label _topLabel = null!;
	private Button _menuBtn = null!;
	private Button _againBtn = null!;

	private ColorRect _pauseOverlay = null!;
	private Label _pauseTitle = null!;
	private Label _pauseSub = null!;

	private Game? _game;
	private int _lastDistMeters;
	private int _lastCoins;
	private bool _saved;

	public override void _Ready()
	{
		ProcessMode = ProcessModeEnum.Always;

		_dist = GetNode<Label>("DistanceLabel");
		_coins = GetNode<Label>("CoinsLabel");
		_help = GetNode<Label>("HelpLabel");
		_goOverlay = GetNode<ColorRect>("GameOverOverlay");
		_goTitle = GetNode<Label>("GameOverOverlay/Center/VBox/Title");
		_goSubtitle = GetNode<Label>("GameOverOverlay/Center/VBox/Subtitle");
		_nameHint = GetNode<Label>("GameOverOverlay/Center/VBox/NameHint");
		_nameEdit = GetNode<LineEdit>("GameOverOverlay/Center/VBox/NameEdit");
		_saveBtn = GetNode<Button>("GameOverOverlay/Center/VBox/SaveButton");
		_topLabel = GetNode<Label>("GameOverOverlay/Center/VBox/TopScoresLabel");
		_menuBtn = GetNode<Button>("GameOverOverlay/Center/VBox/ButtonRow/MenuButton");
		_againBtn = GetNode<Button>("GameOverOverlay/Center/VBox/ButtonRow/AgainButton");

		_pauseOverlay = GetNode<ColorRect>("PauseOverlay");
		_pauseTitle = GetNode<Label>("PauseOverlay/Center/VBox/PauseTitle");
		_pauseSub = GetNode<Label>("PauseOverlay/Center/VBox/PauseSubtitle");

		_saveBtn.Pressed += OnSavePressed;
		_menuBtn.Pressed += OnMenuPressed;
		_againBtn.Pressed += OnAgainPressed;

		_nameEdit.TextSubmitted += _ => OnSavePressed();

		_topLabel.Visible = false;
		_menuBtn.Visible = false;
		_againBtn.Visible = false;
	}

	public void Setup(Game game)
	{
		_game = game;
	}

	public void SetPlayingStart()
	{
		UpdateHud(0, 0);
	}

	public void UpdateHud(double distanceRaw, int coins)
	{
		var m = (int)(distanceRaw / 3.0);
		_dist.Text = $"Dystans: {m} m";
		_coins.Text = $"Monety: {coins}";
	}

	public void ShowGameOver(int distanceMeters, int coins)
	{
		_lastDistMeters = distanceMeters;
		_lastCoins = coins;
		_saved = false;

		_goOverlay.Visible = true;
		_pauseOverlay.Visible = false;
		_goTitle.Text = "Upss! Wpadłeś na przeszkodę";
		_goSubtitle.Text = $"Twój wynik: {distanceMeters} m  ·  monety: {_lastCoins}";
		_nameHint.Visible = true;
		_nameEdit.Visible = true;
		_saveBtn.Visible = true;
		_nameEdit.Text = "";
		_nameEdit.Editable = true;
		_topLabel.Visible = false;
		_menuBtn.Visible = false;
		_againBtn.Visible = false;

		Callable.From(() => _nameEdit.GrabFocus()).CallDeferred();
	}

	public void SetPaused(bool paused)
	{
		_pauseOverlay.Visible = paused;
		if (paused)
		{
			_pauseTitle.Text = "Pauza";
			_pauseSub.Text = "Naciśnij ESC, aby wznowić.";
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is not InputEventKey key || !key.Pressed || key.Echo)
			return;
		if (key.PhysicalKeycode != Key.Escape)
			return;
		if (_goOverlay.Visible)
			return;
		if (_game == null || !_game.CanPause())
			return;
		_game.TogglePause();
		GetViewport().SetInputAsHandled();
	}

	private void OnSavePressed()
	{
		if (_saved)
			return;
		var name = _nameEdit.Text;
		HighScoreStore.AddAndSave(name, _lastDistMeters, _lastCoins);
		_saved = true;

		_nameHint.Visible = false;
		_nameEdit.Visible = false;
		_saveBtn.Visible = false;
		_topLabel.Visible = true;
		_topLabel.Text = "Najlepsza dziesiątka:\n" + HighScoreStore.FormatTop(10);
		_menuBtn.Visible = true;
		_againBtn.Visible = true;
	}

	private void OnMenuPressed()
	{
		GetTree().ChangeSceneToFile("res://scenes/main_menu.tscn");
	}

	private void OnAgainPressed()
	{
		_goOverlay.Visible = false;
		_game?.RestartRun();
	}
}
