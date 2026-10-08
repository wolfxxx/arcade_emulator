// Phase 0 spike: load a libretro core headlessly, run a ROM for N frames, dump the last frame to PNG,
// and check that save states round-trip deterministically.
//
// Usage: Arcade.Spike <core.dll> <rom.zip> [--frames 600] [--out out/frame.png] [--play] [--verbose]

using System.Diagnostics;
using Arcade.Libretro;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Arcade.Spike <core.dll> <rom.zip> [--frames N] [--out file.png] [--play] [--verbose]");
    return 2;
}

var corePath = args[0];
var romPath = args[1];
var frames = 600;
var outPath = Path.Combine("out", Path.GetFileNameWithoutExtension(romPath) + ".png");
var play = false;
var verbose = false;
for (var i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--frames": frames = int.Parse(args[++i]); break;
        case "--out": outPath = args[++i]; break;
        case "--play": play = true; break;
        case "--verbose": verbose = true; break;
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

var repoRoot = FindRepoRoot();
try
{
    host = CoreHost.Load(corePath, new CoreHostOptions
    {
        SystemDirectory = Path.Combine(repoRoot, "system"),
        SaveDirectory = Path.Combine(repoRoot, "saves"),
        Input = input,
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

    var sw = Stopwatch.StartNew();
    for (var f = 0; f < frames; f++)
        host.RunFrame();
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
        var state = host.SaveState();
        for (var f = 0; f < 60; f++) host.RunFrame();
        var first = host.LastFrame.ComputeHash();
        host.LoadState(state);
        for (var f = 0; f < 60; f++) host.RunFrame();
        var second = host.LastFrame.ComputeHash();
        if (first == second)
        {
            Console.WriteLine($"SaveState: {state.Length} bytes, round-trip deterministic");
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
