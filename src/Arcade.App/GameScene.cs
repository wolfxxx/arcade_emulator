using Arcade.App.Ui;
using Arcade.Library;
using SDL;

namespace Arcade.App;

enum GameMode
{
    /// <summary>Someone is playing.</summary>
    Play,
    /// <summary>Attract mode: the game runs its own demo, muted, until a button is pressed or the next game comes up.</summary>
    Attract,
}

/// <summary>A running game, with the pause menu (Esc, P, Guide, or Back+Start held) drawn over it.</summary>
sealed class GameScene(ArcadeApp app, GameSession session, GameMode mode, LibraryGame? game = null) : Scene(app)
{
    const float ChordHoldSeconds = 0.6f;

    Menu? _menu;
    float _chordHeld;
    bool _guideWasHeld;
    float _time;

    public override bool ClockPaced => _menu == null && session.ClockPaced;

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

        if (_menu != null)
        {
            _menu.Update(App.UiInput.Actions, dt);
            if (_menu is { IsOpen: false })
                CloseMenu();
            return;
        }

        _chordHeld = App.GameInput.MenuChordHeld ? _chordHeld + dt : 0;
        var guide = App.GameInput.GuideHeld;
        if (_chordHeld >= ChordHoldSeconds || (guide && !_guideWasHeld))
        {
            _guideWasHeld = guide;
            OpenMenu();
            return;
        }
        _guideWasHeld = guide;

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

    public override bool OnKey(SDL_KeyboardEvent key)
    {
        if (mode == GameMode.Attract)
            return false;
        if (_menu != null)
            return false; // Esc arrives as the menu's Back action
        switch (key.scancode)
        {
            case SDL_Scancode.SDL_SCANCODE_ESCAPE:
            case SDL_Scancode.SDL_SCANCODE_P:
            case SDL_Scancode.SDL_SCANCODE_PAUSE:
                OpenMenu();
                return true;
            case SDL_Scancode.SDL_SCANCODE_F2:
                App.ShowMessage(session.SaveState());
                return true;
            case SDL_Scancode.SDL_SCANCODE_F4:
                App.ShowMessage(session.LoadState());
                return true;
            case SDL_Scancode.SDL_SCANCODE_F3:
                session.Reset();
                App.ShowMessage("Reset");
                return true;
            case SDL_Scancode.SDL_SCANCODE_F12:
                SaveScreenshot();
                return true;
        }
        return false;
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
        _chordHeld = 0;
        var quitLabel = App.DirectLaunch ? "Quit" : "Back to game list";
        _menu = new Menu(session.Title,
        [
            new MenuItem { Label = "Resume" },
            new MenuItem { Label = "Save state", OnAccept = () => App.ShowMessage(session.SaveState()), Hint = "Shortcut: F2" },
            new MenuItem { Label = "Load state", OnAccept = () => App.ShowMessage(session.LoadState()), Enabled = session.HasSavedState, Hint = "Shortcut: F4" },
            new MenuItem { Label = "Reset game", OnAccept = () => { session.Reset(); App.ShowMessage("Reset"); }, Hint = "Shortcut: F3" },
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
            SidePanel =
            [
                ("Move", "Arrow keys"),
                ("Buttons 1–6", "Z X A S Q W"),
                ("Insert coin", "5"),
                ("Start", "1"),
                ("Player 2", "6 coin · 2 start"),
                ("Save · load", "F2 · F4"),
                ("Pause menu", "Esc"),
                ("", "Gamepad: Back = coin, Start = start,"),
                ("", "hold Back + Start for this menu"),
            ],
        };
    }

    void CloseMenu()
    {
        _menu = null;
        session.ResetClock();
        App.UiInput.SuppressHeld();
    }

    public override void Draw(float dt)
    {
        var (w, h) = App.Window.PixelSize;
        session.Draw(App.Video, new RectF(0, 0, w, h), h);

        var r = App.Ui;
        r.Begin(w, h);
        if (mode == GameMode.Attract)
            DrawAttractBanner(r, w, h);
        _menu?.Draw(r, App.Theme, App.UiInput, dt);
        r.End();
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
