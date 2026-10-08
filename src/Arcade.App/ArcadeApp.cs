using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Arcade.App.Browser;
using Arcade.App.Ui;
using Arcade.Libretro;
using Arcade.Library;
using SDL;
using Silk.NET.OpenGL;
using static SDL.SDL3;

namespace Arcade.App;

/// <param name="RomPath">Run this game directly (quitting it exits the app); null opens the game list.</param>
/// <param name="Offscreen">Render hidden at this size (for automated checks with <paramref name="Script"/>).</param>
sealed record AppOptions(string? RomPath, string? Core, bool? Fullscreen, bool Verbose, string? Script = null, (int W, int H)? Offscreen = null);

/// <summary>
/// The application shell: one window, renderer, audio device and input system for the whole
/// session, and a current <see cref="Scene"/> (game list, game, attract mode) that it updates and draws.
/// </summary>
sealed unsafe class ArcadeApp : IDisposable
{
    readonly List<(string Text, long Until)> _toasts = new();
    static readonly ConcurrentQueue<string?> s_pickedFolders = new();
    Action<string>? _onFolderPicked;
    Scene? _scene, _next;
    bool _quit;
    ScriptRunner? _script;
    string? _windowTitle;

    public AppOptions Options { get; }
    public AppPaths Paths { get; } = AppPaths.Discover();
    public Settings Settings { get; }
    public CoreCatalog Catalog { get; }
    public GameLibrary Library { get; private set; } = null!;
    public AppWindow Window { get; private set; } = null!;
    public UiRenderer Ui { get; private set; } = null!;
    public VideoRenderer Video { get; private set; } = null!;
    public ImageCache Images { get; private set; } = null!;
    public Theme Theme { get; private set; } = null!;
    public InputManager GameInput { get; private set; } = null!;
    public UiInput UiInput { get; } = new();
    public AudioOutput Audio { get; private set; } = null!;
    public ArtworkLocator Artwork { get; }

    /// <summary>True when a game was launched from the command line: leaving it quits instead of opening the list.</summary>
    public bool DirectLaunch => Options.RomPath != null;

    /// <summary>Automated runs (offscreen) leave the user's settings, play counts and previews untouched.</summary>
    public bool ReadOnly => Options.Offscreen != null;

    public string SettingsPath => Path.Combine(Paths.Root, "settings.json");
    public string ThemesDir => Path.Combine(Paths.Root, "themes");

    // ---- Background library scan ----
    volatile string? _scanStatus;
    Task<ScanSummary?>? _scan;
    public bool Scanning => _scan is { IsCompleted: false };
    public string? ScanStatus => _scanStatus;
    /// <summary>Raised on the main thread when a scan finishes, so the game list can reload.</summary>
    public event Action<ScanSummary?>? ScanCompleted;

    public ArcadeApp(AppOptions options)
    {
        Options = options;
        Settings = Settings.Load(SettingsPath);
        Settings.ReadOnly = options.Offscreen != null;
        Catalog = new CoreCatalog(Paths);
        Artwork = new ArtworkLocator(Paths.Artwork);
    }

    public int Run()
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_AUDIO | SDL_InitFlags.SDL_INIT_GAMEPAD))
            throw new InvalidOperationException("SDL_Init failed: " + SDL_GetError());
        try
        {
            return RunShell();
        }
        finally
        {
            Dispose();
            SDL_Quit();
        }
    }

    int RunShell()
    {
        // Load the core DATs in the background so the first scan or launch doesn't wait for them.
        _ = Task.Run(() => Catalog.LibraryCores);

        Library = new GameLibrary(Paths.LibraryDb);
        GameInput = new InputManager();
        GameInput.Message += ShowMessage;
        Audio = new AudioOutput();
        if (Options.Offscreen != null)
            Audio.Muted = true;

        // A direct launch loads the game first so the window can take the game's shape.
        GameSession? direct = null;
        if (Options.RomPath != null)
        {
            direct = GameSession.Start(Paths, Catalog, Options.RomPath, Options.Core, GameInput, Audio, Options.Verbose);
            if (Options.Offscreen == null && Library.Find(direct.SetName) != null)
                Library.RecordPlay(direct.SetName, DateTime.Now);
        }

        var fullscreen = Options.Fullscreen ?? Settings.StartFullscreen;
        Window = new AppWindow(direct?.Title ?? "Arcade", direct?.DisplayAspect ?? 16.0 / 9, fullscreen, Options.Offscreen);
        Ui = new UiRenderer(Window.Gl);
        Video = new VideoRenderer(Window.Gl);
        Images = new ImageCache(Window.Gl);
        Theme = Theme.Load(ThemesDir, Paths.Root, Settings.Theme, ShowMessage);
        if (Options.Script != null)
            _script = new ScriptRunner(this, Options.Script);

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        _scene = direct != null ? new GameScene(this, direct, GameMode.Play) : new BrowserScene(this);
        _scene.Enter();
        Loop();
        _scene.Leave();
        return 0;
    }

    void Loop()
    {
        var last = Stopwatch.GetTimestamp();
        while (!_quit)
        {
            PumpEvents();
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(last, now).TotalSeconds;
            last = now;
            var dt = (float)Math.Min(elapsed, 0.1);

            UiInput.Update(dt, GameInput.Pads, keyboardEnabled: !_scene!.CapturesText);
            _script?.Update(dt);
            _scene.Update(dt, elapsed);
            PollBackgroundWork();
            Images.Pump();

            var (w, h) = Window.PixelSize;
            Window.BeginFrame();
            Window.Gl.Viewport(0, 0, (uint)w, (uint)h);
            Window.Gl.ClearColor(0, 0, 0, 1);
            Window.Gl.Clear(ClearBufferMask.ColorBufferBit);
            if (w > 0 && h > 0)
            {
                _scene.Draw(dt);
                DrawToasts(w, h);
            }
            TakePendingScreenshot();
            Window.Swap();

            if (Window.Offscreen)
                SDL_DelayNS(_scene.ClockPaced ? 1_000_000UL : 8_000_000UL);
            else if (_scene.ClockPaced || !Window.VsyncEnabled)
                SDL_DelayNS(1_000_000); // don't spin a core at 100% when vsync isn't pacing the loop

            if (_next != null)
            {
                _scene.Leave();
                _scene = _next;
                _next = null;
                UiInput.SuppressHeld();
                _scene.Enter();
                last = Stopwatch.GetTimestamp();
            }
        }
    }

    public void SwitchTo(Scene scene) => _next = scene;

    public void OnScriptText(string text) => _scene?.OnTextInput(text);

    public void Quit() => _quit = true;

    public string WindowTitle
    {
        set
        {
            if (value == _windowTitle) return;
            _windowTitle = value;
            Window.Title = value;
        }
    }

    // ---- Games ----

    /// <summary>Starts a game from the list. Returns an error message instead of throwing if it can't run.</summary>
    public string? Launch(LibraryGame game, GameMode mode = GameMode.Play)
    {
        try
        {
            var session = GameSession.Start(Paths, Catalog, game.Path, game.CoreOverride ?? (mode == GameMode.Play ? null : game.CoreId),
                mode == GameMode.Attract ? NullInput.Instance : GameInput, Audio, Options.Verbose);
            if (mode == GameMode.Attract || ReadOnly)
                Audio.Muted = true;
            if (mode == GameMode.Play && !ReadOnly)
                Library.RecordPlay(game.SetName, DateTime.Now);
            SwitchTo(new GameScene(this, session, mode, game));
            return null;
        }
        catch (Exception e) when (e is RomSetException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            Console.Error.WriteLine(e.Message);
            return e.Message;
        }
    }

    /// <summary>Leaves a game: back to the game list, or quits after a direct launch.</summary>
    public void ReturnToBrowser(string? selectSet = null)
    {
        if (DirectLaunch)
        {
            Quit();
            return;
        }
        SwitchTo(new BrowserScene(this, selectSet));
    }

    public void ApplyTheme(string id)
    {
        var theme = Theme.Load(ThemesDir, Paths.Root, id, ShowMessage);
        Theme.Dispose();
        Theme = theme;
        Settings.Theme = id;
        Settings.Save(SettingsPath);
    }

    // ---- Artwork ----

    public string CustomSnapPath(string setName) => Path.Combine(Paths.Artwork, "snap-custom", setName + ".png");
    public string AutoSnapPath(string setName) => Path.Combine(Paths.Artwork, "snap-auto", setName + ".png");

    /// <summary>The preview picture for a game: one you chose in the pause menu, then artwork packs, then a screen captured while playing.</summary>
    public string? FindPreview(LibraryGame game)
    {
        var custom = CustomSnapPath(game.SetName);
        if (File.Exists(custom)) return custom;
        var pack = Artwork.Find(ArtworkKind.Snap, game.SetName, game.Title, game.Parent) ?? Artwork.Find(ArtworkKind.Title, game.SetName, game.Title, game.Parent);
        if (pack != null) return pack;
        var auto = AutoSnapPath(game.SetName);
        return File.Exists(auto) ? auto : null;
    }

    public void SavePreview(Rgba32Image image, string path)
    {
        PngEncoder.Save(image, path);
        Images.Invalidate(path);
    }

    // ---- Library scanning ----

    /// <summary>Rescans the library on a background thread; <see cref="ScanCompleted"/> fires when done.</summary>
    public void StartScan()
    {
        if (Scanning)
            return;
        _scanStatus = "Starting scan…";
        var dbPath = Paths.LibraryDb;
        _scan = Task.Run(() =>
        {
            try
            {
                using var library = new GameLibrary(dbPath);
                if (Catalog.LibraryCores.Count == 0)
                {
                    _scanStatus = "No cores installed — run tools/fetch-deps.ps1";
                    return null;
                }
                return Catalog.CreateScanner(library).Scan(new SyncProgress(s => _scanStatus = s));
            }
            catch (Exception e)
            {
                _scanStatus = "Scan failed: " + e.Message;
                Console.Error.WriteLine(e);
                return (ScanSummary?)null;
            }
        });
    }

    sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    void PollBackgroundWork()
    {
        if (_scan is { IsCompleted: true } scan)
        {
            _scan = null;
            var summary = scan.Result;
            if (summary != null)
                ShowMessage($"Library updated: {summary[GameStatus.Playable]} playable game(s)");
            else if (_scanStatus != null)
                ShowMessage(_scanStatus);
            ScanCompleted?.Invoke(summary);
        }

        while (s_pickedFolders.TryDequeue(out var folder))
        {
            var callback = _onFolderPicked;
            _onFolderPicked = null;
            if (folder != null)
                callback?.Invoke(folder);
        }
    }

    /// <summary>Opens the system folder picker; <paramref name="onPicked"/> runs on the main thread if a folder is chosen.</summary>
    public void PickFolder(Action<string> onPicked)
    {
        if (Window.Offscreen)
            return;
        _onFolderPicked = onPicked;
        SDL_ShowOpenFolderDialog(&OnFolderPicked, 0, Window.Handle, (byte*)null, false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnFolderPicked(nint userdata, byte** files, int filter)
    {
        // Called on an SDL thread; hand the result to the main loop.
        s_pickedFolders.Enqueue(files != null && files[0] != null ? Marshal.PtrToStringUTF8((nint)files[0]) : null);
    }

    // ---- Messages ----

    public void ShowMessage(string message)
    {
        Console.WriteLine(message);
        _toasts.Add((message, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3));
        if (_toasts.Count > 3)
            _toasts.RemoveAt(0);
    }

    void DrawToasts(int w, int h)
    {
        var now = Stopwatch.GetTimestamp();
        _toasts.RemoveAll(t => t.Until < now);
        if (_toasts.Count == 0)
            return;
        var r = Ui;
        r.Begin(w, h);
        var font = Theme.Body(r, 28);
        var y = h - r.S(150);
        for (var i = _toasts.Count - 1; i >= 0; i--)
        {
            var (text, until) = _toasts[i];
            var alpha = Math.Clamp((float)((until - now) / (double)Stopwatch.Frequency) / 0.4f, 0, 1);
            var size = UiRenderer.Measure(font, text);
            var maxW = w - r.S(200);
            var boxW = Math.Min(size.X, maxW) + r.S(56);
            var box = new RectF((w - boxW) / 2, y - r.S(64), boxW, r.S(64));
            r.Shadow(box, r.S(32), r.S(16), 0.4f * alpha);
            r.Fill(box, Rgba.Lerp(Theme.Background, Theme.Background2, 0.5f).WithAlpha(0.95f * alpha), r.S(32));
            r.Outline(box, Theme.Accent.WithAlpha(0.8f * alpha), r.S(2), r.S(32));
            r.Text(font, text, box.X + r.S(28), box.Y + (box.H - font.LineHeight) / 2, Theme.Text.WithAlpha(alpha), maxW);
            y -= r.S(80);
        }
        r.End();
    }

    // ---- Events ----

    void PumpEvents()
    {
        SDL_Event e;
        while (SDL_PollEvent(&e))
        {
            switch ((SDL_EventType)e.type)
            {
                case SDL_EventType.SDL_EVENT_QUIT:
                case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    _quit = true;
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                    GameInput.AddGamepad(e.gdevice.which);
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                    GameInput.RemoveGamepad(e.gdevice.which);
                    break;
                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                    OnKey(e.key);
                    break;
                case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                    if (Marshal.PtrToStringUTF8((nint)e.text.text) is { } text)
                        _scene?.OnTextInput(text);
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN when e.button.button == SDL_BUTTON_LEFT:
                    var density = SDL_GetWindowPixelDensity(Window.Handle);
                    UiInput.MarkActivity();
                    _scene?.OnMouseButton(e.button.x * density, e.button.y * density, e.button.clicks);
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                    UiInput.MarkActivity();
                    _scene?.OnMouseWheel(e.wheel.y);
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                    UiInput.MarkActivity();
                    break;
            }
        }
    }

    void OnKey(SDL_KeyboardEvent key)
    {
        var alt = (key.mod & SDL_Keymod.SDL_KMOD_ALT) != 0;
        if (!key.repeat && (key.scancode == SDL_Scancode.SDL_SCANCODE_F11 || (key.scancode == SDL_Scancode.SDL_SCANCODE_RETURN && alt)))
        {
            Window.ToggleFullscreen();
            return;
        }
        if (!key.repeat && key.scancode == SDL_Scancode.SDL_SCANCODE_F12 && _scene is not GameScene)
        {
            SaveWindowScreenshot();
            return;
        }
        if (key.repeat && !_scene!.CapturesText)
            return;
        _scene?.OnKey(key);
    }

    /// <summary>Captures the whole window (UI included) on the next frame.</summary>
    public void SaveWindowScreenshot(string? path = null)
    {
        path ??= Path.Combine(Paths.Root, "screenshots", $"arcade-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        _pendingScreenshot = path;
    }

    string? _pendingScreenshot;

    /// <summary>Called by the script runner and the loop after drawing, before the frame is presented.</summary>
    public void TakePendingScreenshot()
    {
        if (_pendingScreenshot == null)
            return;
        PngEncoder.Save(Window.ReadPixels(), _pendingScreenshot);
        Console.WriteLine($"Screenshot: {Path.GetFullPath(_pendingScreenshot)}");
        _pendingScreenshot = null;
    }

    public void Dispose()
    {
        Images?.Dispose();
        Theme?.Dispose();
        Video?.Dispose();
        Ui?.Dispose();
        Audio?.Dispose();
        GameInput?.Dispose();
        Window?.Dispose();
        Library?.Dispose();
        Images = null!;
        Theme = null!;
        Video = null!;
        Ui = null!;
        Audio = null!;
        GameInput = null!;
        Window = null!;
        Library = null!;
    }
}

/// <summary>Input for attract mode: nobody is playing, so every button reads as released.</summary>
sealed class NullInput : IInputSource
{
    public static readonly NullInput Instance = new();
    public void Poll() { }
    public short GetState(uint port, uint device, uint index, uint id) => 0;
}
