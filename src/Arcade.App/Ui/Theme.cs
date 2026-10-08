using System.Text.Json;
using System.Text.Json.Serialization;
using FontStashSharp;

namespace Arcade.App.Ui;

/// <summary>What a theme.json file contains. Every field is optional; missing ones use the defaults.</summary>
sealed class ThemeFile
{
    public string? Name { get; set; }
    /// <summary>Headings and the logo. A path relative to the theme folder or app folder, or a Windows font file name.</summary>
    public string? TitleFont { get; set; }
    public string? BodyFont { get; set; }
    /// <summary>Multiplies body text sizes, for fonts that run small (e.g. pixel fonts).</summary>
    public float BodyFontScale { get; set; } = 1;
    public float TitleFontScale { get; set; } = 1;
    public Dictionary<string, string> Colors { get; set; } = new();
    public string? BackgroundImage { get; set; }
    public bool Scanlines { get; set; }
    public float CornerRadius { get; set; } = 14;
    /// <summary>"left" or "right": which side the game list is on.</summary>
    public string ListSide { get; set; } = "left";
    public bool UppercaseHeadings { get; set; }
}

/// <summary>A loaded theme: colours, fonts and layout choices used by every screen.</summary>
sealed class Theme : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly FontSystem _title;
    readonly FontSystem _body;

    public string Id { get; }
    public string Name { get; }
    public ThemeFile File { get; }
    public string? BackgroundImage { get; }

    public Rgba Background { get; }
    public Rgba Background2 { get; }
    public Rgba Panel { get; }
    public Rgba PanelBorder { get; }
    public Rgba Text { get; }
    public Rgba TextDim { get; }
    public Rgba Accent { get; }
    public Rgba Accent2 { get; }
    public Rgba Selection { get; }
    public Rgba SelectionText { get; }
    public Rgba Warning { get; }
    public Rgba Error { get; }
    public Rgba Overlay { get; }

    Theme(string id, ThemeFile file, string? folder, string appRoot)
    {
        Id = id;
        File = file;
        Name = file.Name ?? id;

        Rgba C(string key, string fallback) => Rgba.Parse(file.Colors.GetValueOrDefault(key) ?? file.Colors.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? fallback);
        Background = C("background", "#0a0d18");
        Background2 = C("background2", "#1b1033");
        Panel = C("panel", "#ffffff0d");
        PanelBorder = C("panelBorder", "#ffffff1a");
        Text = C("text", "#eef1f8");
        TextDim = C("textDim", "#8e97ad");
        Accent = C("accent", "#ff3d7f");
        Accent2 = C("accent2", "#2fd5ff");
        Selection = C("selection", "#ff3d7f");
        SelectionText = C("selectionText", "#ffffff");
        Warning = C("warning", "#ffb547");
        Error = C("error", "#ff5a5a");
        Overlay = C("overlay", "#05060cd0");

        BackgroundImage = file.BackgroundImage is { } bg ? Resolve(bg, folder, appRoot) : null;

        var settings = new FontSystemSettings { FontResolutionFactor = 1, KernelWidth = 0, KernelHeight = 0 };
        _body = new FontSystem(settings);
        _title = new FontSystem(settings);
        var body = FontChain(file.BodyFont, folder, appRoot, ["segoeui.ttf"]);
        var title = FontChain(file.TitleFont, folder, appRoot, []);
        foreach (var path in body)
            _body.AddFont(System.IO.File.ReadAllBytes(path));
        foreach (var path in title.Concat(body))
            _title.AddFont(System.IO.File.ReadAllBytes(path));
    }

    /// <summary>The requested font first, then Windows fonts so symbols like ★ and accented letters always render.</summary>
    static List<string> FontChain(string? requested, string? folder, string appRoot, string[] defaults)
    {
        var chain = new List<string>();
        if (requested != null && Resolve(requested, folder, appRoot) is { } found)
            chain.Add(found);
        foreach (var name in defaults.Concat(["segoeui.ttf", "seguisym.ttf"]))
            if (Resolve(name, null, appRoot) is { } path && !chain.Contains(path, StringComparer.OrdinalIgnoreCase))
                chain.Add(path);
        if (chain.Count == 0)
            throw new InvalidOperationException("No usable font found (looked for Segoe UI in the Windows fonts folder).");
        return chain;
    }

    static string? Resolve(string path, string? themeFolder, string appRoot)
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates = Path.IsPathRooted(path)
            ? [path]
            : [.. themeFolder == null ? [] : new[] { Path.Combine(themeFolder, path) }, Path.Combine(appRoot, path), Path.Combine(fontsDir, path)];
        return candidates.FirstOrDefault(System.IO.File.Exists);
    }

    /// <summary>Body text at a size given in 1080p design pixels.</summary>
    public SpriteFontBase Body(UiRenderer r, float size) => _body.GetFont(MathF.Round(r.S(size * File.BodyFontScale)));

    public SpriteFontBase Title(UiRenderer r, float size) => _title.GetFont(MathF.Round(r.S(size * File.TitleFontScale)));

    public string Heading(string text) => File.UppercaseHeadings ? text.ToUpperInvariant() : text;

    public float Radius(UiRenderer r) => r.S(File.CornerRadius);

    /// <summary>Themes live in themes/&lt;id&gt;/theme.json. Returns their ids and display names.</summary>
    public static List<(string Id, string Name)> List(string themesDir)
    {
        var list = new List<(string, string)>();
        if (!Directory.Exists(themesDir))
            return list;
        foreach (var dir in Directory.EnumerateDirectories(themesDir).Order())
        {
            var file = Path.Combine(dir, "theme.json");
            if (!System.IO.File.Exists(file))
                continue;
            var id = Path.GetFileName(dir);
            try
            {
                list.Add((id, JsonSerializer.Deserialize<ThemeFile>(System.IO.File.ReadAllText(file), JsonOptions)?.Name ?? id));
            }
            catch (JsonException)
            {
                list.Add((id, id + " (invalid)"));
            }
        }
        return list;
    }

    /// <summary>Loads a theme by id; falls back to the built-in defaults if it is missing or broken.</summary>
    public static Theme Load(string themesDir, string appRoot, string id, Action<string>? warn = null)
    {
        var folder = Path.Combine(themesDir, id);
        var path = Path.Combine(folder, "theme.json");
        if (System.IO.File.Exists(path))
        {
            try
            {
                var file = JsonSerializer.Deserialize<ThemeFile>(System.IO.File.ReadAllText(path), JsonOptions) ?? new ThemeFile();
                return new Theme(id, file, folder, appRoot);
            }
            catch (Exception e) when (e is JsonException or FormatException)
            {
                warn?.Invoke($"Theme '{id}' could not be loaded: {e.Message}");
            }
        }
        else
        {
            warn?.Invoke($"Theme '{id}' not found in {themesDir}");
        }
        return new Theme("default", new ThemeFile { Name = "Default" }, null, appRoot);
    }

    public void Dispose()
    {
        _title.Dispose();
        _body.Dispose();
    }
}
