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
    // ---- Gameplay ----

    /// <summary>Seconds of play kept for rewinding; 0 turns rewind off.</summary>
    public int RewindSeconds { get; set; } = 60;
    /// <summary>How many times normal speed fast-forward runs at.</summary>
    public double FastForwardSpeed { get; set; } = 3;
    /// <summary>Speed of slow motion, as a fraction of normal.</summary>
    public double SlowMotionSpeed { get; set; } = 0.5;

    // ---- Picture ----

    /// <summary>How games look, unless a game has its own settings.</summary>
    public Video.PictureSettings Picture { get; set; } = new();
    /// <summary>Games with picture settings of their own, by set name.</summary>
    public Dictionary<string, Video.PictureSettings> GamePictures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The picture settings a game uses.</summary>
    public Video.PictureSettings PictureFor(string? setName) =>
        setName != null && GamePictures.TryGetValue(setName, out var own) ? own : Picture;

    // ---- Cabinet ----

    /// <summary>Quarter turns clockwise to turn the whole picture (menus too), for a monitor mounted on its side.</summary>
    public int ScreenRotation { get; set; }
    /// <summary>Quarter turns clockwise to show vertical games at by default (0 = upright, with black bars).</summary>
    public int VerticalGameRotation { get; set; }
    /// <summary>When a game's picture is turned, turn the stick with it so up on the stick is up on screen.</summary>
    public bool RotateControls { get; set; } = true;
    /// <summary>Pressing Start inserts a coin first.</summary>
    public bool FreePlay { get; set; }
    /// <summary>
    /// Cabinet mode: always fullscreen with no mouse pointer; the game list hides settings and Quit.
    /// Holding Back for a few seconds in the game list opens the operator menu.
    /// </summary>
    public bool Kiosk { get; set; }

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
