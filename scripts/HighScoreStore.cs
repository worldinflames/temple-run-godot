using System.Text.Json;
using Godot;

namespace TempleRun;

public sealed class HighScoreEntry
{
	public string Name { get; set; } = "";
	public int DistanceMeters { get; set; }
	public int Coins { get; set; }
	public long UnixMs { get; set; }
}

public static class HighScoreStore
{
	private const string FileName = "wyniki.json";
	private const int MaxEntries = 10;
	private static string FilePath => $"user://{FileName}";

	public static IReadOnlyList<HighScoreEntry> Load()
	{
		try
		{
			if (!Godot.FileAccess.FileExists(FilePath))
				return Array.Empty<HighScoreEntry>();

			using var f = Godot.FileAccess.Open(FilePath, Godot.FileAccess.ModeFlags.Read);
			if (f == null)
				return Array.Empty<HighScoreEntry>();

			var json = f.GetAsText();
			var list = JsonSerializer.Deserialize<List<HighScoreEntry>>(json);
			return list ?? new List<HighScoreEntry>();
		}
		catch (Exception e)
		{
			GD.PrintErr($"HighScoreStore.Load: {e.Message}");
			return Array.Empty<HighScoreEntry>();
		}
	}

	public static void AddAndSave(string name, int distanceMeters, int coins)
	{
		var trimmed = string.IsNullOrWhiteSpace(name) ? "Gracz" : name.Trim();
		if (trimmed.Length > 24)
			trimmed = trimmed[..24];

		var list = Load().ToList();
		list.Add(new HighScoreEntry
		{
			Name = trimmed,
			DistanceMeters = distanceMeters,
			Coins = coins,
			UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
		});

		list.Sort(static (a, b) => b.DistanceMeters.CompareTo(a.DistanceMeters));
		if (list.Count > MaxEntries)
			list.RemoveRange(MaxEntries, list.Count - MaxEntries);

		try
		{
			var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
			using var f = Godot.FileAccess.Open(FilePath, Godot.FileAccess.ModeFlags.Write);
			f?.StoreString(json);
		}
		catch (Exception e)
		{
			GD.PrintErr($"HighScoreStore.Save: {e.Message}");
		}
	}

	public static string FormatTop(int count = 10)
	{
		var rows = Load().Take(count).Select((e, i) =>
			$"{i + 1}. {e.Name} — {e.DistanceMeters} m, monety: {e.Coins}");
		return string.Join("\n", rows);
	}
}
