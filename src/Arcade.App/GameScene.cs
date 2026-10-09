using Arcade.App.Controls;
using Arcade.App.Ui;
using Arcade.Library;
using Arcade.Libretro;

namespace Arcade.App;

enum GameMode
{
    /// <summary>Someone is playing.</summary>
    Play,
    /// <summary>Attract mode: the game runs its own demo, muted, until a button is pressed or the next game comes up.</summary>
    Attract,
}

/// <summary>A running game, with the pause menu (Esc, P, Guide, or hotkey-enable + Start) drawn over it.</summary>
sealed class GameScene(ArcadeApp app, GameSession session, GameMode mode, LibraryGame? game = null) : Scene(app)
{
    Menu? _menu;
    ControlsEditor? _controls;
    StatePicker? _states;
    float _time;
    bool _slowMotion;
    bool _rewindWasHeld;
    GameSession? _runAheadReported;

    bool Paused => _menu != null || _controls != null || _states != null;

    public override bool ClockPaced => !Paused && session.ClockPaced;

    public override void Enter()
    {
        session.ConfigureTiming(App.Window.RefreshRate, App.Window.VsyncEnabled);
        App.WindowTitle = mode == GameMode.Attract ? "Arcade — attract mode" : $"{session.Title} — {session.CoreName}";
        if (mode == GameMode.Play)
            foreach (var warning in session.Choice.Warnings)
                App.ShowMessage($"Note: {warning}");
    }

    public override void Leave()
    {
        if (mode == GameMode.Play && !App.ReadOnly && session.PlayTime.TotalSeconds > 15 && session.HasFrame)
        {
            // Give games without artwork a preview picture taken from play.
            var set = session.SetName;
            var hasArt = File.Exists(App.CustomSnapPath(set)) || File.Exists(App.AutoSnapPath(set))
                || App.Artwork.Find(ArtworkKind.Snap, set, session.Title) != null;
            if (!hasArt)
                App.SavePreview(session.CaptureImage(), App.AutoSnapPath(set));
        }
        if (mode == GameMode.Play && !App.ReadOnly && App.Library.Find(session.SetName) != null)
            App.Library.AddPlayTime(session.SetName, session.ActiveTime);
        _bezel?.Texture.Dispose();
        _bezel = null;
        session.Dispose();
    }

    public override void Update(float dt, double elapsed)
    {
        _time += dt;
        if (mode == GameMode.Attract)
        {
            UpdateAttract(dt, elapsed);
            return;
        }

        if (_states != null)
        {
            _states.Update(App.UiInput.Actions, dt);
            if (!_states.IsOpen)
            {
                var done = _states.Done;
                _states = null;
                if (done)
                    CloseMenu(); // saved or loaded: straight back to the game
                else
                    OpenMenu();
            }
            return;
        }

        if (_controls != null)
        {
            _controls.Update(App.UiInput.Actions, dt);
            if (!_controls.IsOpen)
            {
                _controls = null;
                OpenMenu(); // back to the pause menu it was opened from
            }
            return;
        }

        if (_menu != null)
        {
            _menu.Update(App.UiInput.Actions, dt);
            if (_menu is { IsOpen: false } && _controls == null)
                CloseMenu();
            return;
        }

        foreach (var hotkey in App.GameInput.Mapper.FiredHotkeys)
        {
            switch (hotkey)
            {
                case Hotkey.Menu:
                    OpenMenu();
                    return;
                case Hotkey.SaveState:
                    App.ShowMessage(session.SaveState());
                    break;
                case Hotkey.LoadState:
                    App.ShowMessage(session.LoadState());
                    break;
                case Hotkey.Reset:
                    session.Reset();
                    App.ShowMessage("Reset");
                    break;
                case Hotkey.Screenshot:
                    SaveScreenshot();
                    break;
                case Hotkey.ExitGame:
                    App.ReturnToBrowser(session.SetName);
                    return;
                case Hotkey.SlowMotion:
                    _slowMotion = !_slowMotion;
                    App.ShowMessage(_slowMotion ? $"Slow motion ({SpeedText(App.Settings.SlowMotionSpeed)})" : "Normal speed");
                    break;
            }
        }

        var mapper = App.GameInput.Mapper;
        var rewind = mapper.IsHeld(Hotkey.Rewind);
        if (rewind && !_rewindWasHeld && !session.CanRewind)
            App.ShowMessage("Rewind is off — turn it on in Options › Gameplay");
        _rewindWasHeld = rewind;
        session.Rewinding = rewind;
        session.Speed = mapper.IsHeld(Hotkey.FastForward) ? App.Settings.FastForwardSpeed
            : _slowMotion ? App.Settings.SlowMotionSpeed : 1;
        session.Advance(elapsed);
        if (session.RunAheadProblem is { } problem && _runAheadReported != session)
        {
            _runAheadReported = session;
            App.ShowMessage("Run-ahead is off for this game: " + problem);
        }
        if (session.Host.ShutdownRequested)
            App.ReturnToBrowser(session.SetName);
    }

    void UpdateAttract(float dt, double elapsed)
    {
        var actions = App.UiInput.Actions;
        if (actions.Count > 0)
        {
            if (actions.Contains(UiAction.Accept) && game != null)
            {
                // Start playing the game that was on screen.
                session.Dispose();
                if (App.Launch(game) is { } error)
                {
                    App.ShowMessage(error);
                    App.ReturnToBrowser(game.SetName);
                }
            }
            else
            {
                App.ReturnToBrowser(game?.SetName);
            }
            return;
        }

        session.Advance(elapsed);
        if (_time >= App.Settings.AttractSecondsPerGame || session.Host.ShutdownRequested)
            NextAttractGame(App, session, game?.SetName);
    }

    /// <summary>Starts attract mode with a random game, or moves it on to another one.</summary>
    public static void NextAttractGame(ArcadeApp app, GameSession? current, string? currentSet)
    {
        var candidates = app.Library.Games().Where(g => !g.SetName.Equals(currentSet, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0 && currentSet == null)
            return;
        current?.Dispose();
        for (var attempt = 0; attempt < 3 && candidates.Count > 0; attempt++)
        {
            var pick = candidates[Random.Shared.Next(candidates.Count)];
            if (app.Launch(pick, GameMode.Attract) == null)
                return;
            candidates.Remove(pick);
        }
        app.ReturnToBrowser(currentSet);
    }

    void SaveScreenshot()
    {
        var path = Path.Combine(App.Paths.Root, "screenshots", $"{session.SetName}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        Libretro.PngEncoder.Save(session.CaptureImage(), path);
        App.ShowMessage("Screenshot saved");
        Console.WriteLine($"Screenshot: {path}");
    }

    void OpenMenu()
    {
        App.Audio.Pause();
        App.UiInput.SuppressHeld();
        var quitLabel = App.DirectLaunch ? "Quit" : "Back to game list";
        _menu = new Menu(session.Title,
        [
            new MenuItem { Label = "Resume" },
            new MenuItem { Label = "Save state…", OnAccept = () => OpenStates(saving: true), Hint = $"Pick a slot · {HotkeyHint(Hotkey.SaveState, $"saves to slot {session.CurrentSlot}")}" },
            new MenuItem { Label = "Load state…", OnAccept = () => OpenStates(saving: false), Enabled = session.HasSavedState, Hint = $"Pick a slot · {HotkeyHint(Hotkey.LoadState, $"loads slot {session.CurrentSlot}")}" },
            new MenuItem { Label = "Reset game", OnAccept = () => { session.Reset(); App.ShowMessage("Reset"); }, Hint = HotkeyHint(Hotkey.Reset, "resets") },
            new MenuItem
            {
                Label = "Controls…", OnAccept = OpenControls,
                Hint = "Change keys and buttons, this game's button layout and picture",
            },
            new MenuItem
            {
                Label = "Picture…", OnAccept = () => new Video.PictureMenu(App, session, m => _menu = m, OpenMenu).Open(),
                Hint = "Scanlines, CRT effects, size and shape, bezel artwork",
            },
            new MenuItem
            {
                Label = "Cheats…", OnAccept = OpenCheats, Enabled = session.Cheats.Count > 0,
                Hint = session.Cheats.Count > 0 ? $"{session.Cheats.Count} cheat{(session.Cheats.Count == 1 ? "" : "s")} for this game" : "No cheat file for this game",
            },
            new MenuItem
            {
                Label = "Use this screen as preview", Closes = false,
                OnAccept = () =>
                {
                    App.SavePreview(session.CaptureImage(), App.CustomSnapPath(session.SetName));
                    App.ShowMessage("Preview picture saved");
                },
                Hint = "Shows this moment of the game in the game list",
            },
            new MenuItem
            {
                Label = "Fullscreen", Choices = ["Off", "On"], Choice = App.Window.IsFullscreen ? 1 : 0,
                OnChoice = _ => App.Window.ToggleFullscreen(), Hint = "Shortcut: F11 or Alt+Enter",
            },
            new MenuItem { Label = quitLabel, OnAccept = () => App.ReturnToBrowser(session.SetName) },
        ])
        {
            Subtitle = $"{session.CoreName} · {session.MeasuredFps:F1} fps",
            SidePanel = ControlsSummary(),
        };
    }

    void OpenStates(bool saving)
    {
        _states = new StatePicker(App, session, saving);
    }

    /// <summary>The game's cheats, each switched with left/right. They last until the game is closed.</summary>
    void OpenCheats()
    {
        static string Plain(string value) =>
            value.IndexOf(" - ", StringComparison.Ordinal) is var i and > 0 && int.TryParse(value[..i], out _) ? value[(i + 3)..] : value;
        var items = session.Cheats.Select(cheat => new MenuItem
        {
            // FBNeo names them "[Cheat][set.ini] Infinite Lives".
            Label = System.Text.RegularExpressions.Regex.Replace(cheat.Description, @"^(\[[^\]]*\]\s*)+", ""),
            Choices = cheat.Values.Select(Plain).ToList(),
            Choice = Math.Max(0, cheat.Values.ToList().IndexOf(cheat.Value)),
            OnChoice = i => session.SetOption(cheat.Key, cheat.Values[i]),
        }).ToList();
        items.Add(new MenuItem { Label = "Done", OnAccept = OpenMenu });
        _menu = new Menu("Cheats", items)
        {
            Subtitle = "Changes apply straight away and last until you leave the game",
            OnCancel = OpenMenu,
        };
    }

    /// <summary>"Shortcut: F2 saves to slot 1", from the hotkey's first key that works on its own.</summary>
    string HotkeyHint(Hotkey hotkey, string does)
    {
        var binding = App.Controls.HotkeyBindings(hotkey).FirstOrDefault(b => !App.GameInput.Mapper.NeedsEnable(b));
        return binding == default ? "" : $"Shortcut: {binding.Label} {does}";
    }

    void OpenControls()
    {
        _controls = new ControlsEditor(App, new ControlsGame(session.SetName, session.Title, session.InputDescriptors, session.IsVertical))
        {
            Changed = () => App.ConfigureGame(session),
        };
    }

    /// <summary>Player 1's keys and pad buttons, with what each button does in this game.</summary>
    List<(string, string)> ControlsSummary()
    {
        var config = App.Controls;
        var keys = config.PlayerKeys(0);
        var device = App.GameInput.Devices.FirstOrDefault();
        var pad = device != null ? config.DeviceMap(device.Name, device.IsGamepad) : null;
        string Labels(ArcadeControl control)
        {
            var parts = keys.Get(control).Where(b => b.IsKeyboard).Take(1).Concat(pad?.Get(control).Where(b => !b.IsKeyboard).Take(1) ?? []);
            var text = string.Join(" · ", parts.Select(b => b.Label));
            return text.Length == 0 ? "—" : text;
        }
        string Descriptor(JoypadButton id) =>
            session.InputDescriptors.FirstOrDefault(d => d.Port == 0 && d.Device == RetroDevice.Joypad && d.Id == (uint)id)?.Description ?? "";

        var moveKeys = ArcadeControls.Directions.Select(d => keys.Get(d).FirstOrDefault(b => b.IsKeyboard)).Where(b => b != default).Select(b => b.Label).ToList();
        var move = moveKeys.Count == 4 && moveKeys.All(k => k.StartsWith("Arrow ")) ? "Arrow keys" : string.Join(" ", moveKeys);
        var lines = new List<(string, string)> { ("Move", move + (pad != null ? " · stick" : "")) };
        var setup = config.Game(session.SetName);
        var hasDescriptors = session.InputDescriptors.Count > 0;
        for (var panel = 1; panel <= ArcadeControls.ButtonCount && lines.Count < 9; panel++)
        {
            var gameButton = setup.GameButton(panel);
            var name = gameButton == 0 ? "" : Descriptor(ArcadeControls.Button(gameButton).RetroButton());
            if (hasDescriptors ? name.Length == 0 : panel > 6)
                continue;
            lines.Add((name.Length > 0 ? name : $"Button {panel}", Labels(ArcadeControls.Button(panel))));
        }
        lines.Add(("Insert coin", Labels(ArcadeControl.Coin)));
        lines.Add(("Start", Labels(ArcadeControl.Start)));
        var menuKeys = config.HotkeyBindings(Hotkey.Menu).Where(b => !App.GameInput.Mapper.NeedsEnable(b)).Take(2).Select(b => b.Label);
        lines.Add(("Pause menu", string.Join(" · ", menuKeys)));
        if (config.HotkeyEnable is [var enable, ..] && config.HotkeyBindings(Hotkey.Menu).FirstOrDefault(App.GameInput.Mapper.NeedsEnable) is var combo && combo != default)
            lines.Add(("", $"or hold {enable.Label} + {combo.Label}"));
        return lines;
    }

    void CloseMenu()
    {
        _menu = null;
        session.ResetClock();
        App.UiInput.SuppressHeld();
        App.GameInput.Mapper.ResetHotkeys();
    }

    public override void Draw(float dt)
    {
        var (w, h) = App.ScreenSize;
        var picture = App.Settings.PictureFor(session.SetName);
        var screen = new RectF(0, 0, w, h);
        var bezel = Bezel(picture);
        var placed = bezel?.Bezel.Place(screen);
        session.Draw(App.Video, placed?.Window ?? screen, h, picture);

        var r = App.Ui;
        r.Begin(w, h);
        if (bezel != null)
            r.Image(bezel.Value.Texture, placed!.Value.Image);
        if (mode == GameMode.Attract)
            DrawAttractBanner(r, w, h);
        else if (!Paused)
        {
            DrawComboHint(r, w);
            DrawSpeedBadge(r, w);
        }
        if (_states != null)
            _states.Draw(r, App.Theme, App.UiInput, dt);
        else if (_controls != null)
            _controls.Draw(r, App.Theme, App.UiInput, dt);
        else
            _menu?.Draw(r, App.Theme, App.UiInput, dt);
        r.End();
    }

    // ---- Bezel ----

    string? _bezelPath;
    (Video.Bezel Bezel, Texture Texture)? _bezel;
    readonly HashSet<string> _badBezels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bezel artwork to draw around the game, loaded when the setting or the artwork changes.</summary>
    (Video.Bezel Bezel, Texture Texture)? Bezel(Video.PictureSettings picture)
    {
        var path = picture.Bezel ? Video.Bezel.Find(App.Paths.Artwork, session.SetName, game?.Parent ?? App.Library.Find(session.SetName)?.Parent, session.DisplayAspect < 1) : null;
        if (path != null && _badBezels.Contains(path))
            path = null;
        if (path == _bezelPath)
            return _bezel;
        _bezel?.Texture.Dispose();
        _bezel = null;
        _bezelPath = path;
        if (path == null)
            return null;
        try
        {
            var loaded = Video.Bezel.Load(path);
            _bezel = (loaded, new Texture(App.Window.Gl, loaded.Width, loaded.Height, loaded.Pixels));
        }
        catch (InvalidDataException e)
        {
            _badBezels.Add(path);
            Console.Error.WriteLine(e.Message);
            if (mode == GameMode.Play)
                App.ShowMessage(e.Message);
        }
        return _bezel;
    }

    /// <summary>A small bar while a hotkey-enable combination is being held, so people know to keep holding.</summary>
    void DrawComboHint(UiRenderer r, int w)
    {
        var progress = App.GameInput.Mapper.ComboProgress;
        if (progress <= 0.05f)
            return;
        var bar = new RectF(w / 2f - r.S(160), r.S(30), r.S(320), r.S(12));
        r.Fill(bar.Inset(-r.S(6)), Rgba.Black.WithAlpha(0.6f), r.S(12));
        r.Fill(bar with { W = bar.W * progress }, App.Theme.Accent, r.S(6));
    }

    static string SpeedText(double speed) => speed switch
    {
        0.5 => "½×",
        0.25 => "¼×",
        _ => $"{speed:0.##}×",
    };

    /// <summary>A small label in the corner while the game isn't running at normal speed.</summary>
    void DrawSpeedBadge(UiRenderer r, int w)
    {
        var text = session.Rewinding && session.CanRewind ? (session.RewindAtStart ? "« Rewind — as far back as it goes" : "« Rewind")
            : session.Speed > 1 ? $"» Fast-forward {SpeedText(session.Speed)}"
            : session.Speed < 1 ? $"Slow motion {SpeedText(session.Speed)}"
            : null;
        if (text == null)
            return;
        var font = App.Theme.Body(r, 26);
        var size = UiRenderer.Measure(font, text);
        var box = new RectF(w - size.X - r.S(76), r.S(28), size.X + r.S(44), r.S(52));
        r.Fill(box, Rgba.Black.WithAlpha(0.6f), box.H / 2);
        r.Text(font, text, box.X + r.S(22), box.Y + (box.H - font.LineHeight) / 2, App.Theme.Accent);
    }

    void DrawAttractBanner(UiRenderer r, int w, int h)
    {
        var theme = App.Theme;
        var appear = Easing.SmoothStep(_time / 0.6f);
        var bar = new RectF(0, h - r.S(190), w, r.S(190));
        r.FillGradient(bar, Rgba.Black.WithAlpha(0), Rgba.Black.WithAlpha(0.85f * appear));
        var x = r.S(64);
        r.Text(theme.Title(r, 34), game?.Title ?? session.Title, x, bar.Y + r.S(60), theme.Text.WithAlpha(appear), w * 0.6f);
        var meta = string.Join(" · ", new[] { game?.Year, game?.Manufacturer }.Where(s => !string.IsNullOrEmpty(s)));
        r.Text(theme.Body(r, 28), meta, x, bar.Y + r.S(118), theme.TextDim.WithAlpha(appear), w * 0.6f);

        // Blinks like an arcade "insert coin" prompt.
        var blink = (_time % 1.2f) < 0.8f ? 1f : 0.25f;
        var prompt = $"Press {App.UiInput.Label(UiAction.Accept)} to play";
        r.TextRight(theme.Title(r, 26), theme.Heading(prompt), w - x, bar.Y + r.S(66), theme.Accent.WithAlpha(appear * blink));
        r.TextRight(theme.Body(r, 24), "any other button returns to the list", w - x, bar.Y + r.S(120), theme.TextDim.WithAlpha(appear));
    }
}
