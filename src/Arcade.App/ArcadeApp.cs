using System.Diagnostics;
using System.Runtime;
using Arcade.Libretro;
using Arcade.Library;
using SDL;
using Silk.NET.OpenGL;
using static SDL.SDL3;

namespace Arcade.App;

sealed record AppOptions(string RomPath, string? Core, bool Fullscreen, bool Verbose, string? ScreenshotPath, double? ExitAfterSeconds);

/// <summary>Runs one game in a window: picks a core, then loops input → retro_run → audio/video.</summary>
sealed unsafe class ArcadeApp(AppOptions options)
{
    readonly AppPaths _paths = AppPaths.Discover();
    string _setName = "";
    string _title = "";
    string? _message;
    long _messageUntil;
    bool _paused;
    bool _titleDirty;
    double _measuredFps;
    bool _quit;

    public int Run()
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_AUDIO | SDL_InitFlags.SDL_INIT_GAMEPAD))
            throw new InvalidOperationException("SDL_Init failed: " + SDL_GetError());
        try
        {
            return RunGame();
        }
        finally
        {
            SDL_Quit();
        }
    }

    int RunGame()
    {
        _setName = Path.GetFileNameWithoutExtension(options.RomPath);
        var choice = new CoreCatalog(_paths).Choose(options.RomPath, options.Core);
        if (choice.Check is { Status: not RomSetStatus.Complete } check)
            Console.WriteLine($"Warning: {check.Describe()} — trying anyway.");

        using var input = new InputManager();
        using var audio = new AudioOutput();
        input.Message += ShowMessage;

        using var host = CoreHost.Load(choice.DllPath, new CoreHostOptions
        {
            SystemDirectory = _paths.System,
            SaveDirectory = _paths.Saves,
            Input = input,
            Audio = audio,
            // Core debug output can be thousands of lines per second (e.g. MAME unmapped-memory reads), so --verbose stops at Info.
            MinimumLogLevel = options.Verbose ? LogLevel.Info : LogLevel.Warn,
            Log = (level, msg) => Console.WriteLine($"[{level}] {msg}"),
        });
        host.LoadGame(options.RomPath);
        audio.Configure(host.AvInfo.Timing.SampleRate);

        var gameName = choice.Check?.Game?.Description ?? _setName;
        _title = $"{gameName} — {host.Info.Name}";
        using var window = new AppWindow(_title, DisplayAspect(host), options.Fullscreen);
        using var renderer = new VideoRenderer(window.Gl);

        var av = host.AvInfo;
        Console.WriteLine($"Game:   {gameName} [{_setName}] on {host.Info.Name} {host.Info.Version}");
        Console.WriteLine($"Video:  {av.Geometry.BaseWidth}x{av.Geometry.BaseHeight} @ {av.Timing.Fps:F3} Hz, rotation {host.Rotation * 90}°; audio {av.Timing.SampleRate:F0} Hz");

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        Loop(host, window, renderer, input, audio);
        return 0;
    }

    void Loop(CoreHost host, AppWindow window, VideoRenderer renderer, InputManager input, AudioOutput audio)
    {
        var fps = host.AvInfo.Timing.Fps;
        var refresh = window.RefreshRate;

        // If the game's rate is within 1% of the monitor's, run exactly one frame per vsync for
        // perfectly smooth motion and let audio rate control absorb the difference. Otherwise
        // (e.g. a 54.7 Hz game on a 60/144 Hz monitor) pace frames by the clock at the true speed.
        var vsyncLocked = window.VsyncEnabled && refresh > 0 && Math.Abs(fps - refresh) / refresh < 0.01;
        Console.WriteLine($"Sync:   monitor {refresh:F2} Hz, vsync {(window.VsyncEnabled ? "on" : "off")}, mode {(vsyncLocked ? "vsync-locked" : "clock-paced")}");

        var frameSeconds = 1.0 / fps;
        var accumulator = 0.0;
        var last = Stopwatch.GetTimestamp();
        var start = last;
        var statsStart = last;
        var statsFrames = host.FrameCount;
        var swapAverage = 1.0 / Math.Max(refresh, 1);
        var exitChordFrames = 0;

        while (!_quit)
        {
            PumpEvents(host, window, input, audio);

            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(last, now).TotalSeconds;
            last = now;

            if (!_paused)
            {
                if (vsyncLocked)
                {
                    RunFrame(host, audio);
                }
                else
                {
                    // Cap catch-up so a stall (window drag, breakpoint) doesn't fast-forward.
                    accumulator = Math.Min(accumulator + elapsed, frameSeconds * 4);
                    while (accumulator >= frameSeconds)
                    {
                        RunFrame(host, audio);
                        accumulator -= frameSeconds;
                    }
                }
            }

            exitChordFrames = input.ExitChordHeld ? exitChordFrames + 1 : 0;
            if (exitChordFrames > fps * 1.5)
                _quit = true;

            var (w, h) = window.PixelSize;
            renderer.Render(host.LastFrame, host.Rotation, host.AvInfo.Geometry.AspectRatio, w, h);
            window.Swap();

            if (!window.VsyncEnabled && !vsyncLocked)
                SDL_DelayNS(1_000_000); // avoid spinning a core at 100% without vsync

            if (vsyncLocked)
            {
                // If swaps stop blocking (minimised window, forced-off vsync), we'd run too fast.
                swapAverage += (elapsed - swapAverage) * 0.05;
                if (swapAverage < 0.5 / refresh)
                {
                    vsyncLocked = false;
                    accumulator = 0;
                    Console.WriteLine("Sync:   vsync stopped blocking; switching to clock-paced");
                }
            }

            // Once a second: show speed in the title and the console when verbose.
            if (Stopwatch.GetElapsedTime(statsStart).TotalSeconds >= 1)
            {
                var measured = (host.FrameCount - statsFrames) / Stopwatch.GetElapsedTime(statsStart).TotalSeconds;
                if (options.Verbose)
                    Console.WriteLine($"Stats:  {measured:F2} fps (target {fps:F2}), audio queue {audio.QueuedSeconds * 1000:F0} ms (low {audio.LowestQueuedSeconds * 1000:F0}), rate x{audio.CurrentRatio:F4}");
                statsStart = Stopwatch.GetTimestamp();
                statsFrames = host.FrameCount;
                audio.ResetStats();
                _measuredFps = measured;
                _titleDirty = true;
            }

            if (_titleDirty)
            {
                UpdateTitle(window);
                _titleDirty = false;
            }

            if (options.ExitAfterSeconds is { } limit && Stopwatch.GetElapsedTime(start).TotalSeconds >= limit)
            {
                if (options.ScreenshotPath != null)
                    SaveScreenshot(window, options.ScreenshotPath);
                var total = Stopwatch.GetElapsedTime(start).TotalSeconds;
                Console.WriteLine($"Ran:    {host.FrameCount} frames in {total:F2} s = {host.FrameCount / total:F2} fps (target {fps:F2})");
                _quit = true;
            }
        }
    }

    static void RunFrame(CoreHost host, AudioOutput audio)
    {
        host.RunFrame();
        audio.UpdateRate();
        if (host.AvInfo.Timing.SampleRate > 0)
            audio.Configure(host.AvInfo.Timing.SampleRate); // no-op unless the core changed rate
    }

    void PumpEvents(CoreHost host, AppWindow window, InputManager input, AudioOutput audio)
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
                    input.AddGamepad(e.gdevice.which);
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                    input.RemoveGamepad(e.gdevice.which);
                    break;
                case SDL_EventType.SDL_EVENT_KEY_DOWN when !e.key.repeat:
                    OnKey(e.key, host, window, audio);
                    break;
            }
        }
        if (host.ShutdownRequested)
            _quit = true;
    }

    void OnKey(SDL_KeyboardEvent key, CoreHost host, AppWindow window, AudioOutput audio)
    {
        var alt = (key.mod & SDL_Keymod.SDL_KMOD_ALT) != 0;
        switch (key.scancode)
        {
            case SDL_Scancode.SDL_SCANCODE_ESCAPE:
                _quit = true;
                break;
            case SDL_Scancode.SDL_SCANCODE_F11:
            case SDL_Scancode.SDL_SCANCODE_RETURN when alt:
                window.ToggleFullscreen();
                break;
            case SDL_Scancode.SDL_SCANCODE_P:
            case SDL_Scancode.SDL_SCANCODE_PAUSE:
                _paused = !_paused;
                ShowMessage(_paused ? "Paused" : "Resumed");
                break;
            case SDL_Scancode.SDL_SCANCODE_F3:
                host.Reset();
                ShowMessage("Reset");
                break;
            case SDL_Scancode.SDL_SCANCODE_F2:
                SaveState(host);
                break;
            case SDL_Scancode.SDL_SCANCODE_F4:
                LoadState(host, audio);
                break;
            case SDL_Scancode.SDL_SCANCODE_F12:
                SaveScreenshot(window, Path.Combine(_paths.Root, "screenshots", $"{_setName}-{DateTime.Now:yyyyMMdd-HHmmss}.png"));
                break;
        }
    }

    string StatePath => Path.Combine(_paths.Saves, _setName + ".state");

    void SaveState(CoreHost host)
    {
        try
        {
            File.WriteAllBytes(StatePath, host.SaveState());
            ShowMessage("State saved");
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException)
        {
            ShowMessage("Save state failed: " + ex.Message);
        }
    }

    void LoadState(CoreHost host, AudioOutput audio)
    {
        if (!File.Exists(StatePath))
        {
            ShowMessage("No saved state yet (F2 to save)");
            return;
        }
        try
        {
            host.LoadState(File.ReadAllBytes(StatePath));
            audio.Flush();
            ShowMessage("State loaded");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ShowMessage("Load state failed: " + ex.Message);
        }
    }

    void SaveScreenshot(AppWindow window, string path)
    {
        var (w, h) = window.PixelSize;
        var pixels = new byte[w * h * 4];
        fixed (byte* p = pixels)
            window.Gl.ReadPixels(0, 0, (uint)w, (uint)h, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, p);
        // OpenGL rows run bottom-up; PNG rows run top-down.
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < h; y++)
            pixels.AsSpan((h - 1 - y) * w * 4, w * 4).CopyTo(flipped.AsSpan(y * w * 4));
        PngEncoder.Save(new Rgba32Image(w, h, flipped), path);
        ShowMessage("Screenshot saved");
        Console.WriteLine($"Screenshot: {Path.GetFullPath(path)}");
    }

    void ShowMessage(string message)
    {
        // Until the on-screen UI exists (Phase 3), messages go to the title bar and console.
        Console.WriteLine(message);
        _message = message;
        _messageUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
        _titleDirty = true;
    }

    void UpdateTitle(AppWindow window)
    {
        var suffix = _paused ? " — PAUSED" : $" — {_measuredFps:F1} fps";
        if (_message != null && Stopwatch.GetTimestamp() < _messageUntil)
            suffix += " — " + _message;
        window.Title = _title + suffix;
    }

    /// <summary>Aspect ratio of the final image as shown on screen (after rotation).</summary>
    static double DisplayAspect(CoreHost host)
    {
        var g = host.AvInfo.Geometry;
        if (g.AspectRatio > 0)
            return g.AspectRatio;
        return host.Rotation % 2 == 1 ? (double)g.BaseHeight / g.BaseWidth : (double)g.BaseWidth / g.BaseHeight;
    }
}
