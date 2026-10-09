using System.Globalization;
using Arcade.App.Ui;

namespace Arcade.App.Video;

/// <summary>
/// The Picture menu: style, scaling, shape, background and bezel, plus the style's own settings.
/// From the pause menu it edits the settings the game uses (all games, or this game only) and
/// sits at the side so the game shows the changes as they're made; from Options it edits the
/// settings for all games.
/// </summary>
sealed class PictureMenu(ArcadeApp app, GameSession? session, Action<Menu> show, Action back)
{
    static readonly PictureScaling[] Scalings = Enum.GetValues<PictureScaling>();
    static readonly PictureShape[] Shapes = Enum.GetValues<PictureShape>();
    static readonly PictureBackground[] Backgrounds = Enum.GetValues<PictureBackground>();

    Settings Settings => app.Settings;
    bool OwnSettings => session != null && Settings.GamePictures.ContainsKey(session.SetName);
    PictureSettings Picture => Settings.PictureFor(session?.SetName);

    void Save() => Settings.Save(app.SettingsPath);

    public void Open(int selected = 0)
    {
        app.Shaders.Refresh();
        var picture = Picture;
        var styles = app.Shaders.All();
        var styleIndex = Math.Max(0, styles.ToList().FindIndex(s => s.Id.Equals(picture.Style, StringComparison.OrdinalIgnoreCase)));
        var items = new List<MenuItem>();

        if (session != null)
            items.Add(new MenuItem
            {
                Label = "Settings for", Choices = ["All games", "This game only"], Choice = OwnSettings ? 1 : 0,
                OnChoice = i =>
                {
                    if (i == 1)
                        Settings.GamePictures[session.SetName] = Settings.Picture.Clone();
                    else
                        Settings.GamePictures.Remove(session.SetName);
                    Save();
                    Open(0); // show the values now in use
                },
                Hint = OwnSettings ? "This game has its own picture settings" : "Changes apply to every game without settings of its own",
            });

        var styleRow = items.Count;
        items.Add(new MenuItem
        {
            Label = "Style", Choices = styles.Select(s => s.Name).ToList(), Choice = styleIndex,
            OnChoice = i => { picture.Style = styles[i].Id; Save(); },
            OnAccept = () => OpenStyles(styles, styleRow),
            Closes = false,
            Hint = styles.Count > ShaderLibrary.BuiltIn.Count
                ? $"{styles.Count} styles, including presets in the shaders folder · {app.UiInput.Label(UiAction.Accept)} for the list"
                : "Add RetroArch GLSL presets (.glslp) to the shaders folder for more",
        });
        items.Add(new MenuItem
        {
            Label = "Size", Choices = ["Fit the screen", "Whole multiples", "Stretch"],
            Choice = Array.IndexOf(Scalings, picture.Scaling),
            OnChoice = i => { picture.Scaling = Scalings[i]; Save(); },
            Hint = "Whole multiples keeps every line of the game the same height — best for scanlines",
        });
        items.Add(new MenuItem
        {
            Label = "Shape", Choices = ["Arcade monitor", "Square pixels"],
            Choice = Array.IndexOf(Shapes, picture.Shape),
            OnChoice = i => { picture.Shape = Shapes[i]; Save(); },
            Hint = "Arcade games were made for 4:3 monitors, so their pixels weren't quite square",
        });
        items.Add(new MenuItem
        {
            Label = "Background", Choices = ["Black", "Glow from the game"],
            Choice = Array.IndexOf(Backgrounds, picture.Background),
            OnChoice = i => { picture.Background = Backgrounds[i]; Save(); },
            Hint = "What fills the space around the game",
        });
        items.Add(new MenuItem
        {
            Label = "Bezel artwork", Choices = ["Off", "On"], Choice = picture.Bezel ? 1 : 0,
            OnChoice = i => { picture.Bezel = i == 1; Save(); },
            Hint = BezelHint(),
        });
        items.Add(new MenuItem
        {
            Label = "Adjust style…", OnAccept = OpenParameters,
            Hint = "Scanline strength, curvature, glow and the like, for the current style",
        });
        items.Add(new MenuItem { Label = "Done", OnAccept = back });

        show(new Menu("Picture", items)
        {
            Subtitle = session == null ? "For all games without settings of their own" : session.Title,
            OnCancel = back,
            Docked = session != null,
            Selected = Math.Clamp(selected, 0, items.Count - 1),
        });
    }

    string BezelHint()
    {
        if (session == null)
            return "Shows artwork/bezels/<set>.png around a game when there is one";
        var game = app.Library.Find(session.SetName);
        var path = Bezel.Find(app.Paths.Artwork, session.SetName, game?.Parent, session.DisplayAspect < 1);
        return path == null
            ? "None for this game — put a PNG named after the game in artwork/bezels"
            : $"Using {Path.GetRelativePath(app.Paths.Artwork, path).Replace('\\', '/')}";
    }

    void OpenStyles(IReadOnlyList<PictureStyle> styles, int returnTo)
    {
        var picture = Picture;
        var items = styles.Select(style => new MenuItem
        {
            Label = style.Name,
            Hint = style.Description,
            OnAccept = () => { picture.Style = style.Id; Save(); Open(returnTo); },
        }).ToList();
        show(new Menu("Style", items)
        {
            Subtitle = $"{styles.Count} styles",
            OnCancel = () => Open(returnTo),
            Docked = session != null,
            Selected = Math.Max(0, styles.ToList().FindIndex(s => s.Id.Equals(picture.Style, StringComparison.OrdinalIgnoreCase))),
        });
    }

    void OpenParameters()
    {
        var picture = Picture;
        var parameters = app.Video.ParametersFor(picture.Style);
        var activeStyle = app.Video.ActiveStyle ?? picture.Style;
        var values = picture.Parameters.TryGetValue(activeStyle, out var v) ? v : null;
        var items = new List<MenuItem>();
        var returnTo = session != null ? 6 : 5;
        foreach (var parameter in parameters)
        {
            var steps = Steps(parameter);
            var current = values != null && values.TryGetValue(parameter.Name, out var chosen) ? chosen : parameter.Default;
            var index = 0;
            for (var i = 1; i < steps.Count; i++)
                if (Math.Abs(steps[i] - current) < Math.Abs(steps[index] - current))
                    index = i;
            items.Add(new MenuItem
            {
                Label = parameter.Description.Length > 0 ? parameter.Description : parameter.Name,
                Choices = steps.Select(s => Format(s, parameter)).ToList(),
                Choice = index,
                OnChoice = i =>
                {
                    var own = PictureFor(activeStyle);
                    if (Math.Abs(steps[i] - parameter.Default) < parameter.Step / 2)
                        own.Remove(parameter.Name);
                    else
                        own[parameter.Name] = steps[i];
                    Save();
                },
                Hint = $"{parameter.Name} · default {Format(parameter.Default, parameter)}",
            });
        }
        if (items.Count == 0)
            items.Add(new MenuItem { Label = "This style has no settings", Enabled = false });
        else
            items.Add(new MenuItem
            {
                Label = "Back to the style's defaults",
                OnAccept = () => { picture.Parameters.Remove(activeStyle); Save(); OpenParameters(); },
            });
        items.Add(new MenuItem { Label = "Done", OnAccept = () => Open(returnTo) });

        var style = app.Shaders.Find(activeStyle);
        show(new Menu("Adjust style", items)
        {
            Subtitle = style?.Name ?? activeStyle,
            OnCancel = () => Open(returnTo),
            Docked = session != null,
        });
    }

    Dictionary<string, float> PictureFor(string style)
    {
        var picture = Picture;
        if (!picture.Parameters.TryGetValue(style, out var values))
            picture.Parameters[style] = values = new Dictionary<string, float>();
        return values;
    }

    /// <summary>The values a setting can take, from its minimum to maximum by its step (at most about 100).</summary>
    static List<float> Steps(ShaderParameter p)
    {
        var step = p.Step > 0 ? p.Step : (p.Max - p.Min) / 20;
        var count = step > 0 ? (int)Math.Round((p.Max - p.Min) / step) : 0;
        if (count > 100)
        {
            step = (p.Max - p.Min) / 100;
            count = 100;
        }
        var list = new List<float>();
        for (var i = 0; i <= count; i++)
            list.Add(MathF.Round((p.Min + i * step) * 10000) / 10000);
        if (list.Count == 0)
            list.Add(p.Default);
        return list;
    }

    static string Format(float value, ShaderParameter p)
    {
        // As many decimals as the step needs.
        var decimals = p.Step >= 1 ? 0 : p.Step >= 0.1f ? 1 : p.Step >= 0.01f ? 2 : 3;
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
