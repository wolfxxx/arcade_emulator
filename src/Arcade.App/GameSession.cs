using System.Diagnostics;
using Arcade.App.Ui;
using Arcade.Libretro;
using Arcade.Library;

namespace Arcade.App;

/// <summary>
/// One running game: the loaded core, its frame pacing (with fast-forward, slow motion and rewind)
/// and save states. The app creates a session per game and disposes it to return to the game list;
/// the window and audio device live on.
/// </summary>
sealed class GameSession : IDisposable
{
    readonly CoreHost _host;
    readonly AudioOutput _audio;
    readonly long _started = Stopwatch.GetTimestamp();
    RewindBuffer? _rewind;
    byte[] _stateBuffer = [];
    bool _clockMode;

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
    /// <summary>Time the game actually ran (menus and pauses not counted), for play statistics.</summary>
    public TimeSpan ActiveTime { get; private set; }
    /// <summary>True when frames are paced by the clock rather than by vsync, so the loop must not spin.</summary>
    public bool ClockPaced => !_vsyncLocked;

    GameSession(CoreHost host, AudioOutput audio, string romPath, CoreChoice choice, string title)
    {
        _host = host;
        _audio = audio;
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
        // High score tables survive switching off (FBNeo also needs system/fbneo/hiscore.dat; MAME 2003-Plus has its own).
        ["fbneo-hiscores"] = "enabled",
    };

    /// <summary>Picks a core, loads it and the game. Throws <see cref="RomSetException"/> if no core can run it.</summary>
    public static GameSession Start(AppPaths paths, CoreCatalog catalog, string romPath, string? core, IInputSource input, AudioOutput audio, bool verbose)
    {
        var choice = catalog.Choose(romPath, core);
        HiscoreExtras.Apply(paths.System);
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
        HiscoreExtras.Apply(paths.System); // in case the core only just wrote its hiscore.dat

        var title = choice.Check?.Game?.Description ?? Path.GetFileNameWithoutExtension(romPath);
        var session = new GameSession(host, audio, romPath, choice, title);
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

    /// <summary>Game speed: 1 normally, above 1 to fast-forward, below 1 for slow motion.</summary>
    public double Speed { get; set; } = 1;

    /// <summary>Frames of input lag to take away by running ahead (0 = off).</summary>
    public int RunAheadFrames { get; set; }

    RunAheadEstimator? _estimator;

    Action? _cannotTime;

    /// <summary>Timings thrown away (the game didn't replay the same way twice) before giving up on a game never timed.</summary>
    const int UnrepeatableLimit = 6;

    /// <summary>
    /// Run as far ahead as this game needs, timing it as it's played. <paramref name="known"/> is
    /// what an earlier session measured; <paramref name="measured"/> gets each new result to keep,
    /// and <paramref name="cannotTime"/> is called if the game turns out not to replay exactly
    /// enough to be timed (run-ahead then stays off).
    /// </summary>
    public void UseAutomaticRunAhead(int? known, Action<int> measured, Action cannotTime)
    {
        _cannotTime = cannotTime;
        _estimator = new RunAheadEstimator(known);
        RunAheadFrames = _estimator.Frames;
        _host.LagMeasured += lag =>
        {
            if (_estimator.Add(lag))
                measured(_estimator.Frames);
            RunAheadFrames = _estimator.Frames;
        };
    }

    public bool AutomaticRunAhead => _estimator != null;

    /// <summary>Why run-ahead couldn't be used for this game, or null.</summary>
    public string? RunAheadProblem => _host.RunAheadProblem;

    /// <summary>While true, the game runs backwards through the rewind history.</summary>
    public bool Rewinding { get; set; }

    /// <summary>True when rewinding has gone back as far as the history reaches.</summary>
    public bool RewindAtStart { get; private set; }

    public bool CanRewind => _rewind != null;

    /// <summary>Keeps up to this many seconds of play for rewinding (0 turns it off).</summary>
    public void EnableRewind(int seconds)
    {
        _rewind = null;
        var size = _host.StateSize;
        if (seconds <= 0 || size <= 0)
            return;
        var steps = (int)Math.Ceiling(seconds * Fps);
        // Each step is a compressed difference, typically a few percent of a state; the arena is
        // only committed as it fills, and when it runs out the oldest steps go first.
        const long MinBytes = 16 << 20, MaxBytes = 256 << 20;
        var capacity = (int)Math.Clamp((long)size * steps / 8, MinBytes, MaxBytes);
        _rewind = new RewindBuffer(capacity, steps);
    }

    /// <summary>Seconds of play the rewind history currently holds.</summary>
    public double RewindSeconds => (_rewind?.Count ?? 0) / Fps;

    /// <summary>Runs as many frames as the time since the last call is worth.</summary>
    public void Advance(double elapsedSeconds)
    {
        ActiveTime += TimeSpan.FromSeconds(Math.Min(elapsedSeconds, 0.25));
        var rewinding = Rewinding && _rewind != null;
        var speed = rewinding ? 1 : Speed;
        _host.FastForwarding = speed > 1;
        // Running ahead costs extra frames each frame; it only matters when playing at normal speed.
        _host.RunAhead = speed == 1 && !rewinding ? RunAheadFrames : 0;
        if (_estimator is { HasEstimate: false } && _host.UnrepeatableTimings >= UnrepeatableLimit && _cannotTime != null)
        {
            _estimator.GiveUp();
            _cannotTime();
        }
        _host.MeasureLag = _estimator is { Done: false } && speed == 1 && !rewinding;
        _audio.Speed = speed;
        _audio.Suppressed = rewinding;
        if (!rewinding)
            RewindAtStart = false;

        // Normal play keeps one frame per vsync if it can; other speeds and rewinding go by the clock.
        var clockMode = !_vsyncLocked || speed != 1 || rewinding;
        if (clockMode != _clockMode)
        {
            _clockMode = clockMode;
            _accumulator = 0;
        }

        if (!clockMode)
        {
            Tick(rewinding);
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
            var frameSeconds = 1.0 / (Fps * speed);
            // Cap catch-up so a stall (window drag, breakpoint) doesn't race ahead.
            _accumulator = Math.Min(_accumulator + elapsedSeconds, Math.Max(frameSeconds * 4, 1.0 / 15));
            while (_accumulator >= frameSeconds)
            {
                Tick(rewinding);
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

    void Tick(bool rewinding)
    {
        if (rewinding)
        {
            StepBack();
            return;
        }
        RunFrame();
        Record();
    }

    void RunFrame()
    {
        _host.RunFrame();
        _audio.UpdateRate();
        if (_host.AvInfo.Timing.SampleRate > 0)
            _audio.Configure(_host.AvInfo.Timing.SampleRate); // no-op unless the core changed rate
    }

    /// <summary>Adds the state after this frame to the rewind history.</summary>
    void Record()
    {
        if (_rewind == null)
            return;
        var size = _host.StateSize;
        if (size <= 0)
            return;
        if (_stateBuffer.Length != size)
            _stateBuffer = new byte[size];
        if (_host.TrySaveState(_stateBuffer))
            _rewind.Push(_stateBuffer);
    }

    /// <summary>Goes back one frame: restores the previous state and runs it to get its picture.</summary>
    void StepBack()
    {
        if (!_rewind!.TryStepBack(out var state))
        {
            RewindAtStart = true; // hold the oldest moment on screen
            return;
        }
        try
        {
            _host.LoadState(state);
        }
        catch (InvalidOperationException)
        {
            _rewind.Clear();
            return;
        }
        RunFrame();
    }

    /// <summary>Extra quarter turns clockwise to show the picture at (the cabinet or game setting), on top of the core's own rotation.</summary>
    public int PictureRotation { get; set; }

    public RectF Draw(VideoRenderer renderer, RectF area, int windowHeight, Video.PictureSettings picture, float alpha = 1)
    {
        // The renderer counts anticlockwise turns, like the core does.
        var turns = (uint)((_host.Rotation + 4 - PictureRotation % 4) % 4);
        return renderer.Render(_host.LastFrame, turns, DisplayAspect, area, windowHeight, picture, Rewinding && CanRewind ? -1 : 1, alpha);
    }

    /// <summary>The game's picture size as the core makes it, before turning.</summary>
    public (int Width, int Height) FrameSize => (_host.LastFrame.Width, _host.LastFrame.Height);

    /// <summary>The picture is shown a quarter turn round from how the core makes it.</summary>
    public bool PictureTurned => (_host.Rotation + 4 - PictureRotation % 4) % 2 == 1;

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

    // ---- Save states ----

    /// <summary>Where this game's save states live; set by the app before play starts.</summary>
    public StateSlots? Slots { get; set; }

    /// <summary>The slot the save and load hotkeys use: the last one saved or loaded.</summary>
    public int CurrentSlot { get; set; } = 1;

    public bool HasSavedState => Slots?.All().Any(s => !s.IsEmpty) ?? false;

    public string SaveState(int? slot = null)
    {
        if (Slots == null)
            return "Save states are off";
        var n = slot ?? CurrentSlot;
        try
        {
            Slots.Save(n, _host.SaveState(), HasFrame ? CaptureImage() : null);
            CurrentSlot = n;
            return $"Saved to slot {n}";
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return "Save state failed: " + ex.Message;
        }
    }

    public string LoadState(int? slot = null)
    {
        if (Slots == null)
            return "Save states are off";
        var n = slot ?? CurrentSlot;
        try
        {
            if (Slots.Load(n) is not { } state)
                return $"Slot {n} is empty";
            _host.LoadState(state);
            _audio.Flush();
            CurrentSlot = n;
            return $"Loaded slot {n}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return "Load state failed: " + ex.Message;
        }
    }

    // ---- Cheats ----

    /// <summary>
    /// Cheats the core offers for this game. FBNeo reads cheat files (system/fbneo/cheats/&lt;set&gt;.ini)
    /// and lists each cheat as a core option whose values are "0 - Disabled", "1 - Enabled"…
    /// </summary>
    public IReadOnlyList<CoreOption> Cheats =>
        _host.Options.Values.Where(o => o.Key.StartsWith("fbneo-cheat-", StringComparison.Ordinal)).ToList();

    public void SetOption(string key, string value) => _host.SetOption(key, value);

    public void Reset() => _host.Reset();

    /// <summary>The current game picture as an upright image (rotation applied), for previews.</summary>
    public Rgba32Image CaptureImage() => _host.LastFrame.ToRgba32().RotateCcw(_host.Rotation);

    bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (RunAheadFrames > 0 || AutomaticRunAhead)
            Console.WriteLine($"Input:  run-ahead {_host.RunAheadProblem ?? "stayed on"} at {RunAheadFrames} frame(s) ({_host.RunAheadChecks} self-checks)");
        _host.Dispose();
        _audio.Stop();
    }
}
