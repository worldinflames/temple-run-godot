using Godot;

namespace TempleRun;

public partial class MainMenu : Control
{
	private Button _btnNew = null!;
	private Button _btnScores = null!;
	private Button _btnExit = null!;
	private Panel _scoresPanel = null!;
	private Label _scoresText = null!;
	private Button _scoresBack = null!;

	public override void _Ready()
	{
		_btnNew = GetNode<Button>("Root/VBox/BtnNew");
		_btnScores = GetNode<Button>("Root/VBox/BtnScores");
		_btnExit = GetNode<Button>("Root/VBox/BtnExit");
		_scoresPanel = GetNode<Panel>("ScoresPanel");
		_scoresText = GetNode<Label>("ScoresPanel/Margin/VBox/ScoresText");
		_scoresBack = GetNode<Button>("ScoresPanel/Margin/VBox/BtnBack");

		_btnNew.Pressed += OnNewGame;
		_btnScores.Pressed += OnShowScores;
		_btnExit.Pressed += OnExit;
		_scoresBack.Pressed += OnScoresBack;

		_scoresPanel.Visible = false;
	}

	private void OnNewGame()
	{
		GetTree().ChangeSceneToFile("res://scenes/game.tscn");
	}

	private void OnShowScores()
	{
		var body = HighScoreStore.FormatTop(10);
		_scoresText.Text = string.IsNullOrWhiteSpace(body)
			? "Brak zapisanych wyników."
			: body;
		_scoresPanel.Visible = true;
	}

	private void OnScoresBack()
	{
		_scoresPanel.Visible = false;
	}

	private void OnExit()
	{
		GetTree().Quit();
	}
}
