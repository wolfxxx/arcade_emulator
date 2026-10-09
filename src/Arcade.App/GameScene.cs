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
    float _time;

    bool Paused => _menu != null || _controls != null;

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
            }
        }

        session.Advance(elapsed);
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
            new MenuItem { Label = "Save state", OnAccept = () => App.ShowMessage(session.SaveState()), Hint = "Shortcut: F2" },
            new MenuItem { Label = "Load state", OnAccept = () => App.ShowMessage(session.LoadState()), Enabled = session.HasSavedState, Hint = "Shortcut: F4" },
            new MenuItem { Label = "Reset game", OnAccept = () => { session.Reset(); App.ShowMessage("Reset"); }, Hint = "Shortcut: F3" },
            new MenuItem
            {
                Label = "Controls…", OnAccept = OpenControls,
                Hint = "Change keys and buttons, this game's button layout and picture",
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
        session.Draw(App.Video, new RectF(0, 0, w, h), h);

        var r = App.Ui;
        r.Begin(w, h);
        if (mode == GameMode.Attract)
            DrawAttractBanner(r, w, h);
        else if (!Paused)
            DrawComboHint(r, w);
        if (_controls != null)
            _controls.Draw(r, App.Theme, App.UiInput, dt);
        else
            _menu?.Draw(r, App.Theme, App.UiInput, dt);
        r.End();
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
