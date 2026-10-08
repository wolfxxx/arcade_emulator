using System.Text.Json;
using Arcade.App.Ui;

namespace Arcade.App;

enum SortOrder { Title, Year, Manufacturer, MostPlayed, RecentlyPlayed }

/// <summary>User preferences, stored as settings.json in the app folder.</summary>
sealed class Settings
{
    public string Theme { get; set; } = "midnight";
    public bool StartFullscreen { get; set; }
    public bool ShowClones { get; set; } = true;
    public SortOrder Sort { get; set; } = SortOrder.Title;
    /// <summary>Minutes of no input in the game list before attract mode starts; 0 turns it off.</summary>
    public int AttractMinutes { get; set; } = 3;
    /// <summary>Seconds each game plays in attract mode.</summary>
    public int AttractSecondsPerGame { get; set; } = 45;
    public string? LastTab { get; set; }
    public string? LastGame { get; set; }

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Ui.Theme.JsonOptions) ?? new Settings();
        }
        catch (JsonException e)
        {
            Console.Error.WriteLine($"Ignoring settings.json: {e.Message}");
        }
        return new Settings();
    }

    /// <summary>Set for automated runs so they don't overwrite the user's settings.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ReadOnly { get; set; }

    public void Save(string path)
    {
        if (ReadOnly)
            return;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Ui.Theme.JsonOptions));
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"Could not save settings: {e.Message}");
        }
    }
}
