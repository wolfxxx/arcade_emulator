using System.Diagnostics;
using Arcade.App.Ui;
using Arcade.Libretro;
using Arcade.Library;

namespace Arcade.App;

/// <summary>
/// One running game: the loaded core, its frame pacing, and save states. The app creates a
/// session per game and disposes it to return to the game list; the window and audio device live on.
/// </summary>
sealed class GameSession : IDisposable
{
    readonly CoreHost _host;
    readonly AudioOutput _audio;
    readonly AppPaths _paths;
    readonly long _started = Stopwatch.GetTimestamp();

    bool _vsyncLocked;
    double _refresh;
    double _accumulator;
    double _swapAverage;
    long _statsStart = Stopwatch.GetTimestamp();
    long _statsFrames;

    public string RomPath { get; }
    public string SetName { get; }
    public string Title { get; }
    public string CoreName => _host.Info.Name;
    public CoreChoice Choice { get; }
    public CoreHost Host => _host;
    public double MeasuredFps { get; private set; }
    public TimeSpan PlayTime => Stopwatch.GetElapsedTime(_started);
    /// <summary>True when frames are paced by the clock rather than by vsync, so the loop must not spin.</summary>
    public bool ClockPaced => !_vsyncLocked;

    GameSession(CoreHost host, AudioOutput audio, AppPaths paths, string romPath, CoreChoice choice, string title)
    {
        _host = host;
        _audio = audio;
        _paths = paths;
        RomPath = romPath;
        SetName = Path.GetFileNameWithoutExtension(romPath);
        Choice = choice;
        Title = title;
    }

    /// <summary>Core settings we change from the core's defaults. Unknown keys or values are ignored by the host.</summary>
    static readonly Dictionary<string, string> DefaultOptions = new()
    {
        // MAME 2003-Plus shows a copyright disclaimer and driver warnings before each game; the
        // game list already explains a set's status, so go straight to the game (and to its demo in attract mode).
        ["mame2003-plus_skip_disclaimer"] = "enabled",
        ["mame2003-plus_skip_warnings"] = "enabled",
    };

    /// <summary>Picks a core, loads it and the game. Throws <see cref="RomSetException"/> if no core can run it.</summary>
    public static GameSession Start(AppPaths paths, CoreCatalog catalog, string romPath, string? core, IInputSource input, AudioOutput audio, bool verbose)
    {
        var choice = catalog.Choose(romPath, core);
        var host = CoreHost.Load(choice.DllPath, new CoreHostOptions
        {
            SystemDirectory = paths.System,
            SaveDirectory = paths.Saves,
            Input = input,
            Audio = audio,
            // Core debug output can be thousands of lines per second (e.g. MAME unmapped-memory reads), so verbose stops at Info.
            MinimumLogLevel = verbose ? LogLevel.Info : LogLevel.Warn,
            Log = (level, msg) => Console.WriteLine($"[{level}] {msg}"),
            OptionOverrides = DefaultOptions,
        });
        try
        {
            host.LoadGame(romPath);
        }
        catch
        {
            host.Dispose();
            throw;
        }
        audio.Configure(host.AvInfo.Timing.SampleRate);

        var title = choice.Check?.Game?.Description ?? Path.GetFileNameWithoutExtension(romPath);
        var session = new GameSession(host, audio, paths, romPath, choice, title);
        var av = host.AvInfo;
        Console.WriteLine($"Game:   {title} [{session.SetName}] on {host.Info.Name} {host.Info.Version}");
        Console.WriteLine($"Video:  {av.Geometry.BaseWidth}x{av.Geometry.BaseHeight} @ {av.Timing.Fps:F3} Hz, rotation {host.Rotation * 90}°; audio {av.Timing.SampleRate:F0} Hz");
        return session;
    }

    public double Fps => _host.AvInfo.Timing.Fps;

    /// <summary>
    /// Chooses the pacing. If the game's rate is within 1% of the monitor's, run exactly one frame
    /// per vsync for perfectly smooth motion and let audio rate control absorb the difference.
    /// Otherwise (e.g. a 54.7 Hz game on a 60/144 Hz monitor) pace frames by the clock.
    /// </summary>
    public void ConfigureTiming(double refresh, bool vsync)
    {
        _refresh = refresh;
        _vsyncLocked = vsync && refresh > 0 && Math.Abs(Fps - refresh) / refresh < 0.01;
        _swapAverage = 1.0 / Math.Max(refresh, 1);
        _accumulator = 0;
        Console.WriteLine($"Sync:   monitor {refresh:F2} Hz, vsync {(vsync ? "on" : "off")}, mode {(_vsyncLocked ? "vsync-locked" : "clock-paced")}");
    }

    /// <summary>Runs as many frames as the time since the last call is worth.</summary>
    public void Advance(double elapsedSeconds)
    {
        if (_vsyncLocked)
        {
            RunFrame();
            // If swaps stop blocking (minimised window, vsync forced off), we'd run too fast.
            _swapAverage += (elapsedSeconds - _swapAverage) * 0.05;
            if (_swapAverage < 0.5 / _refresh)
            {
                _vsyncLocked = false;
                _accumulator = 0;
                Console.WriteLine("Sync:   vsync stopped blocking; switching to clock-paced");
            }
        }
        else
        {
            var frameSeconds = 1.0 / Fps;
            // Cap catch-up so a stall (window drag, breakpoint) doesn't fast-forward.
            _accumulator = Math.Min(_accumulator + elapsedSeconds, frameSeconds * 4);
            while (_accumulator >= frameSeconds)
            {
                RunFrame();
                _accumulator -= frameSeconds;
            }
        }

        var statsElapsed = Stopwatch.GetElapsedTime(_statsStart).TotalSeconds;
        if (statsElapsed >= 1)
        {
            MeasuredFps = (_host.FrameCount - _statsFrames) / statsElapsed;
            _statsStart = Stopwatch.GetTimestamp();
            _statsFrames = _host.FrameCount;
        }
    }

    /// <summary>Forget time spent paused, so resuming doesn't try to catch up.</summary>
    public void ResetClock()
    {
        _accumulator = 0;
        _statsStart = Stopwatch.GetTimestamp();
        _statsFrames = _host.FrameCount;
    }

    void RunFrame()
    {
        _host.RunFrame();
        _audio.UpdateRate();
        if (_host.AvInfo.Timing.SampleRate > 0)
            _audio.Configure(_host.AvInfo.Timing.SampleRate); // no-op unless the core changed rate
    }

    /// <summary>Extra quarter turns clockwise to show the picture at (the cabinet or game setting), on top of the core's own rotation.</summary>
    public int PictureRotation { get; set; }

    public RectF Draw(VideoRenderer renderer, RectF area, int windowHeight, float alpha = 1)
    {
        // The renderer counts anticlockwise turns, like the core does.
        var turns = (uint)((_host.Rotation + 4 - PictureRotation % 4) % 4);
        return renderer.Render(_host.LastFrame, turns, (float)DisplayAspect, area, windowHeight, alpha);
    }

    /// <summary>Aspect ratio of the game as designed, after the core's rotation.</summary>
    double UprightAspect
    {
        get
        {
            var g = _host.AvInfo.Geometry;
            if (g.AspectRatio > 0)
                return g.AspectRatio;
            return _host.Rotation % 2 == 1 ? (double)g.BaseHeight / g.BaseWidth : (double)g.BaseWidth / g.BaseHeight;
        }
    }

    /// <summary>Aspect ratio of the final image as shown on screen (after both rotations).</summary>
    public double DisplayAspect => PictureRotation % 2 == 1 ? 1 / UprightAspect : UprightAspect;

    /// <summary>True when the game's screen is taller than wide (a vertical game), before any picture rotation.</summary>
    public bool IsVertical => UprightAspect < 1;

    /// <summary>What the core says each RetroPad button does in this game, by player port.</summary>
    public IReadOnlyList<InputDescriptor> InputDescriptors => _host.InputDescriptors;

    public bool HasFrame => _host.LastFrame.Width > 0;

    string StatePath => Path.Combine(_paths.Saves, SetName + ".state");

    public bool HasSavedState => File.Exists(StatePath);

    public string SaveState()
    {
        try
        {
            File.WriteAllBytes(StatePath, _host.SaveState());
            return "State saved";
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException)
        {
            return "Save state failed: " + ex.Message;
        }
    }

    public string LoadState()
    {
        if (!HasSavedState)
            return "No saved state yet";
        try
        {
            _host.LoadState(File.ReadAllBytes(StatePath));
            _audio.Flush();
            return "State loaded";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return "Load state failed: " + ex.Message;
        }
    }

    public void Reset() => _host.Reset();

    /// <summary>The current game picture as an upright image (rotation applied), for previews.</summary>
    public Rgba32Image CaptureImage() => _host.LastFrame.ToRgba32().RotateCcw(_host.Rotation);

    bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _host.Dispose();
        _audio.Stop();
    }
}
