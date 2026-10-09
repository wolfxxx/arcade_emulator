// Phase 0 spike: load a libretro core headlessly, run a ROM for N frames, dump the last frame to PNG,
// and check that save states round-trip deterministically.
//
// Usage: Arcade.Spike <core.dll> <rom.zip> [--frames 600] [--out out/frame.png] [--play] [--audio-meter] [--options] [--verbose]

using System.Diagnostics;
using Arcade.Libretro;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Arcade.Spike <core.dll> <rom.zip> [--frames N] [--out file.png] [--play] [--audio-meter] [--options] [--verbose]");
    return 2;
}

var corePath = args[0];
var romPath = args[1];
var frames = 600;
var outPath = Path.Combine("out", Path.GetFileNameWithoutExtension(romPath) + ".png");
var play = false;
var verbose = false;
var audioMeter = false;
var listOptions = false;
for (var i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--frames": frames = int.Parse(args[++i]); break;
        case "--out": outPath = args[++i]; break;
        case "--play": play = true; break;
        case "--verbose": verbose = true; break;
        case "--audio-meter": audioMeter = true; break;
        case "--options": listOptions = true; break;
        default: Console.Error.WriteLine($"Unknown argument {args[i]}"); return 2;
    }
}

CoreHost? host = null;
var input = new ScriptedInput(() => host?.FrameCount ?? 0);
if (play)
{
    // Insert a coin, press start, then wiggle the stick and fire so we see gameplay.
    input.Press(120, JoypadButton.Select).Press(180, JoypadButton.Start);
    for (var f = 240; f < frames; f += 60)
        input.Press(f, (f / 60 % 2 == 0) ? JoypadButton.Left : JoypadButton.Right, 30).Press(f + 10, JoypadButton.B, 4);
}

var meter = new AudioMeter();
var repoRoot = FindRepoRoot();
try
{
    host = CoreHost.Load(corePath, new CoreHostOptions
    {
        SystemDirectory = Path.Combine(repoRoot, "system"),
        SaveDirectory = Path.Combine(repoRoot, "saves"),
        Input = input,
        Audio = meter,
        MinimumLogLevel = verbose ? LogLevel.Debug : LogLevel.Warn,
        Log = (level, msg) => Console.WriteLine($"  [{level}] {msg}"),
    });
    Console.WriteLine($"Core:      {host.Info.Name} {host.Info.Version} (ext: {host.Info.ValidExtensions}, fullpath: {host.Info.NeedFullPath})");

    host.LoadGame(romPath);
    var av = host.AvInfo;
    Console.WriteLine($"Game:      {Path.GetFileName(romPath)}");
    Console.WriteLine($"Video:     {av.Geometry.BaseWidth}x{av.Geometry.BaseHeight} (max {av.Geometry.MaxWidth}x{av.Geometry.MaxHeight}), aspect {av.Geometry.AspectRatio:F3}, rotation {host.Rotation * 90}°");
    Console.WriteLine($"Timing:    {av.Timing.Fps:F4} fps, audio {av.Timing.SampleRate:F0} Hz");
    Console.WriteLine($"Options:   {host.Options.Count} declared");
    if (listOptions)
        foreach (var option in host.Options.Values)
            Console.WriteLine($"  {option.Key} = {option.Value}  [{string.Join(" | ", option.Values)}]");

    var sw = Stopwatch.StartNew();
    for (var f = 0; f < frames; f++)
    {
        host.RunFrame();
        // Once per emulated second, report how loud the core's output was.
        if (audioMeter && (f + 1) % (int)Math.Round(av.Timing.Fps) == 0)
        {
            var (peak, nonSilent) = meter.TakeSecond();
            Console.WriteLine($"  second {(f + 1) / (int)Math.Round(av.Timing.Fps),3}: peak {peak,5} ({peak / 327.67,5:F1}% of max), non-silent samples {nonSilent * 100:F1}%");
        }
    }
    sw.Stop();
    var speed = frames / av.Timing.Fps / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"Ran:       {frames} frames in {sw.ElapsedMilliseconds} ms ({speed:F1}x realtime)");
    Console.WriteLine($"Audio:     {host.AudioFramesReceived} stereo frames (expected ~{frames / av.Timing.Fps * av.Timing.SampleRate:F0})");

    var frame = host.LastFrame;
    var image = frame.ToRgba32().RotateCcw(host.Rotation);
    PngEncoder.Save(image, outPath);
    Console.WriteLine($"Frame:     {frame.Width}x{frame.Height} {frame.Format}, hash {frame.ComputeHash():x16}{(image.IsUniform() ? "  (WARNING: blank screen)" : "")}");
    Console.WriteLine($"Saved:     {Path.GetFullPath(outPath)}");

    // Determinism check: save, run 60 frames, load, run the same 60 frames, compare.
    try
    {
        var timer = Stopwatch.StartNew();
        var state = host.SaveState();
        var saveMs = timer.Elapsed.TotalMilliseconds;
        for (var f = 0; f < 60; f++) host.RunFrame();
        var first = host.LastFrame.ComputeHash();
        host.LoadState(state);
        for (var f = 0; f < 60; f++) host.RunFrame();
        var second = host.LastFrame.ComputeHash();
        if (first == second)
        {
            Console.WriteLine($"SaveState: {state.Length} bytes in {saveMs:F2} ms, round-trip deterministic");
        }
        else
        {
            // Distinguish "state misses something" (reloads agree with each other) from true nondeterminism.
            host.LoadState(state);
            for (var f = 0; f < 60; f++) host.RunFrame();
            var third = host.LastFrame.ComputeHash();
            Console.WriteLine($"SaveState: {state.Length} bytes, round-trip MISMATCH vs uninterrupted run; " +
                (second == third ? "repeated loads agree (state is incomplete in the core)" : "repeated loads differ (nondeterministic)"));
        }
    }
    catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
    {
        Console.WriteLine($"SaveState: unsupported ({ex.Message})");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
finally
{
    host?.Dispose();
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "ArcadeEmulator.sln")))
            return dir.FullName;
    return Directory.GetCurrentDirectory();
}


/// <summary>Tracks peak level and the share of non-zero samples, to tell "silent" from "not playing".</summary>
sealed class AudioMeter : IAudioSink
{
    int _peak;
    long _samples, _nonZero;

    public void Write(ReadOnlySpan<short> interleavedStereo)
    {
        foreach (var s in interleavedStereo)
        {
            var a = Math.Abs((int)s);
            if (a > _peak) _peak = a;
            if (a > 64) _nonZero++; // ignore tiny DC/dither noise
        }
        _samples += interleavedStereo.Length;
    }

    public (int Peak, double NonSilentShare) TakeSecond()
    {
        var result = (_peak, _samples == 0 ? 0 : (double)_nonZero / _samples);
        _peak = 0;
        _samples = _nonZero = 0;
        return result;
    }
}