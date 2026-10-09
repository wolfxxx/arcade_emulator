using System.Diagnostics;
using Arcade.App.Ui;
using Arcade.Libretro;
using SDL;
using static SDL.SDL3;

namespace Arcade.App.Video;

/// <summary>
/// <c>shaders [--check] [folder]</c>: lists the picture styles, and with --check compiles and runs
/// each one on a test picture in a hidden window, to find presets that won't work before choosing them.
/// </summary>
static class ShaderCommand
{
    public static int Run(AppPaths paths, string[] args)
    {
        var check = args.Contains("--check");
        var folder = args.FirstOrDefault(a => !a.StartsWith("--")) ?? paths.Shaders;
        var library = new ShaderLibrary(Path.GetFullPath(folder));
        var styles = library.All();
        if (!check)
        {
            foreach (var style in styles)
                Console.WriteLine($"{(style.BuiltIn ? "built-in" : "folder  ")}  {style.Name,-40} {style.Description}");
            if (styles.All(s => s.BuiltIn))
                Console.WriteLine($"\nNo presets in {library.UserFolder} yet. Put RetroArch GLSL presets (.glslp) there to add styles.");
            return 0;
        }

        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine("Could not start SDL: " + SDL_GetError());
            return 1;
        }
        const int width = 640, height = 480;
        using var window = new AppWindow("shader check", 4.0 / 3, false, (width, height));
        string? report = null;
        using var video = new VideoRenderer(window.Gl, library, message => report = message);
        var frames = TestFrames();
        int ok = 0, failed = 0, blank = 0;
        foreach (var style in styles)
        {
            report = null;
            var timer = Stopwatch.StartNew();
            var picture = new PictureSettings { Style = style.Id };
            string? problem = null;
            try
            {
                foreach (var frame in frames)
                {
                    window.BeginFrame();
                    window.Gl.Viewport(0, 0, width, height);
                    window.Gl.Clear(Silk.NET.OpenGL.ClearBufferMask.ColorBufferBit);
                    video.Render(frame, 0, 4.0 / 3, new RectF(0, 0, width, height), height, picture);
                }
                var error = window.Gl.GetError();
                if (report != null || video.ActiveStyle != style.Id)
                    problem = report ?? "failed";
                else if (error != Silk.NET.OpenGL.GLEnum.NoError)
                    problem = $"graphics error {error}";
                else if (window.ReadPixels().IsUniform())
                {
                    problem = "draws a blank picture";
                    blank++;
                }
            }
            catch (Exception e) when (e is ShaderException or InvalidOperationException)
            {
                problem = e.Message;
            }
            if (problem == null)
            {
                ok++;
                Console.WriteLine($"ok      {style.Name} ({timer.ElapsedMilliseconds} ms)");
            }
            else
            {
                if (!problem.StartsWith("draws")) failed++;
                Console.WriteLine($"PROBLEM {style.Name}: {problem}");
                if (args.Contains("--verbose") && video.ActiveStyle == style.Id)
                    foreach (var line in video.DescribeChain())
                        Console.WriteLine(line);
            }
        }
        Console.WriteLine($"\n{ok} of {styles.Count} work; {failed} fail; {blank} draw nothing on the test picture.");
        return 0;
    }

    /// <summary>A few frames of a moving test picture, so shaders that blend frames get some history.</summary>
    static List<VideoFrame> TestFrames()
    {
        const int w = 320, h = 240;
        var list = new List<VideoFrame>();
        for (var n = 1; n <= 4; n++)
        {
            var data = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    var bar = (x + n * 3) / 40;
                    data[i] = (byte)((bar & 1) * 200);         // blue
                    data[i + 1] = (byte)((bar & 2) * 100);     // green
                    data[i + 2] = (byte)((bar & 4) * 50 + y / 2); // red
                }
            list.Add(VideoFrame.Create(data, w, h, w * 4, PixelFormat.Xrgb8888, n));
        }
        return list;
    }
}
