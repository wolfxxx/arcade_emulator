using Arcade.App;
using Arcade.App.Ui;
using Arcade.App.Video;
using Arcade.Libretro;
using SDL;
using static SDL.SDL3;

namespace Arcade.Tests;

public class ShaderPresetTests
{
    static Func<string, string> Files(Dictionary<string, string> files) =>
        path => files.TryGetValue(path, out var text) ? text : throw new FileNotFoundException(path);

    [Fact]
    public void Reads_passes_with_their_scaling_filtering_and_textures()
    {
        var files = new Dictionary<string, string>
        {
            ["C:/shaders/crt/look.glslp"] = """
                # a comment
                shaders = "3"
                shader0 = ../common/linearize.glsl
                filter_linear0 = false
                float_framebuffer0 = true
                scale_type0 = source
                scale0 = 2.0
                shader1 = shaders/blur.glsl
                scale_type_x1 = absolute
                scale_x1 = 640
                scale_type_y1 = viewport
                scale_y1 = 0.5
                alias1 = Blurred
                wrap_mode1 = repeat
                shader2 = shaders/final.glsl
                textures = "MASK;NOISE"
                MASK = masks/slot.png
                MASK_linear = false
                NOISE = noise.png
                parameters = "STRENGTH;GLOW"
                STRENGTH = 0.75
                GLOW = 0.2 # trailing comment
                """,
        };
        var preset = ShaderPreset.Parse("C:/shaders/crt/look.glslp", Files(files));

        Assert.Equal(3, preset.Passes.Count);
        var first = preset.Passes[0];
        Assert.Equal("C:/shaders/common/linearize.glsl", first.Path);
        Assert.False(first.FilterLinear);
        Assert.True(first.FloatFramebuffer);
        Assert.Equal((ScaleType.Source, 2f), (first.ScaleTypeX, first.ScaleX));
        var blur = preset.Passes[1];
        Assert.Equal("C:/shaders/crt/shaders/blur.glsl", blur.Path);
        Assert.Equal((ScaleType.Absolute, 640f, ScaleType.Viewport, 0.5f), (blur.ScaleTypeX, blur.ScaleX, blur.ScaleTypeY, blur.ScaleY));
        Assert.Equal("Blurred", blur.Alias);
        Assert.Equal(WrapMode.Repeat, blur.Wrap);
        Assert.Null(blur.FilterLinear);
        // The last pass without a scale draws at the game's size on screen.
        Assert.True(preset.Passes[2].IsViewportSized);

        Assert.Equal(["MASK", "NOISE"], preset.Textures.Select(t => t.Name));
        Assert.Equal("C:/shaders/crt/masks/slot.png", preset.Textures[0].Path);
        Assert.False(preset.Textures[0].Linear);
        Assert.True(preset.Textures[1].Linear);
        Assert.Equal(0.75f, preset.Parameters["STRENGTH"]);
        Assert.Equal(0.2f, preset.Parameters["GLOW"]);
        Assert.False(preset.Parameters.ContainsKey("scale0"));
    }

    [Fact]
    public void A_preset_can_start_from_another_and_change_its_values()
    {
        var files = new Dictionary<string, string>
        {
            ["builtin:/crt.glslp"] = "shaders = 1\nshader0 = crt.glsl\nCURVATURE = 0.0\nGLOW = 0.1\n",
            ["builtin:/crt-curved.glslp"] = "#reference \"crt.glslp\"\nCURVATURE = 1.0\n",
        };
        var preset = ShaderPreset.Parse("builtin:/crt-curved.glslp", Files(files));
        Assert.Equal("builtin:/crt.glsl", Assert.Single(preset.Passes).Path);
        Assert.Equal(1.0f, preset.Parameters["CURVATURE"]);
        Assert.Equal(0.1f, preset.Parameters["GLOW"]);
    }

    [Fact]
    public void Vulkan_presets_and_missing_passes_are_explained()
    {
        var slang = Assert.Throws<ShaderException>(() => ShaderPreset.Parse("x/crt.slangp", _ => ""));
        Assert.Contains("GLSL", slang.Message);
        var missing = Assert.Throws<ShaderException>(() => ShaderPreset.Parse("a.glslp", _ => "shaders = 2\nshader0 = a.glsl\n"));
        Assert.Contains("shader1", missing.Message);
    }

    [Fact]
    public void Shader_files_list_their_settings_and_compile_as_glsl_330()
    {
        var source = new ShaderSource("look.glsl", """
            #version 130
            #pragma parameter STRENGTH "Scanline strength" 0.5 0.0 1.0 0.05
            #pragma parameter GLOW "Glow" 0.1 0.0 0.5
            #extension GL_ARB_shader_texture_lod : enable
            void main() {}
            """);
        Assert.Equal([("STRENGTH", "Scanline strength", 0.5f, 0f, 1f, 0.05f), ("GLOW", "Glow", 0.1f, 0f, 0.5f, 0.025f)],
            source.Parameters.Select(p => (p.Name, p.Description, p.Default, p.Min, p.Max, p.Step)));
        var vertex = source.Stage(vertex: true);
        Assert.StartsWith("#version 330 core\n#extension GL_ARB_shader_texture_lod : enable\n#define VERTEX\n", vertex);
        Assert.DoesNotContain("#version 130", vertex);
        Assert.Contains("#define FRAGMENT", source.Stage(vertex: false));
        // With a compatibility context the file's own version is kept, as RetroArch does.
        Assert.StartsWith("#version 130\n#extension", source.Stage(vertex: true, compatibility: true));
        Assert.StartsWith("#define VERTEX", new ShaderSource("old.glsl", "void main() {}").Stage(vertex: true, compatibility: true));
    }

    [Fact]
    public void The_built_in_styles_all_load()
    {
        var library = new ShaderLibrary(Path.Combine(Path.GetTempPath(), "no-such-folder"));
        foreach (var style in ShaderLibrary.BuiltIn)
        {
            var preset = library.Load(style.Id);
            Assert.NotEmpty(preset.Passes);
            foreach (var pass in preset.Passes)
                Assert.NotEmpty(library.ReadText(pass.Path));
        }
        Assert.Equal(1.0f, library.Load("crt-curved").Parameters["CURVATURE"]);
    }

    [Fact]
    public void Presets_in_the_shaders_folder_are_listed_by_path()
    {
        var folder = Directory.CreateTempSubdirectory("arcade-shaders").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "crt"));
            File.WriteAllText(Path.Combine(folder, "crt", "crt-lottes.glslp"), "shaders = 1\nshader0 = crt-lottes.glsl\n");
            File.WriteAllText(Path.Combine(folder, "mine.glslp"), "shaders = 1\nshader0 = mine.glsl\n");
            var library = new ShaderLibrary(folder);
            var user = library.All().Where(s => !s.BuiltIn).ToList();
            Assert.Equal(["crt/crt-lottes.glslp", "mine.glslp"], user.Select(s => s.Id));
            Assert.Equal("crt-lottes (crt)", user[0].Name);
            Assert.EndsWith("crt/crt-lottes.glsl", library.Load("crt/crt-lottes.glslp").Passes[0].Path);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class PictureLayoutTests
{
    static readonly RectF Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void Fit_keeps_the_original_monitor_shape()
    {
        var rect = PictureLayout.Place(Screen, 320, 224, turned: false, 4.0 / 3, PictureScaling.Fit, PictureShape.Original);
        Assert.Equal(new RectF(240, 0, 1440, 1080), rect);
    }

    [Fact]
    public void Whole_multiples_give_every_line_the_same_height()
    {
        // 224 lines: 4 × 224 = 896 fits in 1080, 5 × doesn't.
        var rect = PictureLayout.Place(Screen, 320, 224, turned: false, 4.0 / 3, PictureScaling.Integer, PictureShape.Original);
        Assert.Equal(896, rect.H);
        Assert.Equal(1195, rect.W); // 896 × 4/3, rounded
        Assert.Equal((1920 - 1195) / 2f, rect.X, 0.5f);
        Assert.Equal(92, rect.Y);
    }

    [Fact]
    public void Whole_multiples_with_square_pixels_are_pixel_perfect()
    {
        var rect = PictureLayout.Place(Screen, 320, 224, turned: false, 4.0 / 3, PictureScaling.Integer, PictureShape.SquarePixels);
        Assert.Equal(new RectF(320, 92, 1280, 896), rect);
    }

    [Fact]
    public void A_turned_game_counts_its_lines_across_the_screen()
    {
        // A vertical game (224 lines of 256) shown upright: its lines run up and down the screen.
        var rect = PictureLayout.Place(Screen, 256, 224, turned: true, 3.0 / 4, PictureScaling.Integer, PictureShape.Original);
        // Across: 3 × 224 = 672 wide; down: 672 × 4/3 = 896 tall (4 × 224 would need 1195 rows).
        Assert.Equal((672f, 896f), (rect.W, rect.H));
        var square = PictureLayout.Place(Screen, 256, 224, turned: true, 3.0 / 4, PictureScaling.Integer, PictureShape.SquarePixels);
        Assert.Equal((896f, 1024f), (square.W, square.H)); // 4 × 224 across, 4 × 256 down
    }

    [Fact]
    public void Stretch_fills_the_area()
    {
        Assert.Equal(Screen, PictureLayout.Place(Screen, 320, 224, false, 4.0 / 3, PictureScaling.Stretch, PictureShape.Original));
    }
}

public class BezelTests
{
    [Fact]
    public void The_window_is_the_see_through_area_around_the_middle()
    {
        const int w = 160, h = 90;
        var rgba = new byte[w * h * 4];
        Array.Fill(rgba, (byte)255);
        void Clear(int x0, int y0, int x1, int y1)
        {
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                    rgba[(y * w + x) * 4 + 3] = 0;
        }
        Clear(40, 10, 120, 80);   // the game's window
        Clear(2, 2, 10, 10);      // a see-through detail elsewhere doesn't count
        Assert.Equal(new RectF(40, 10, 80, 70), Bezel.FindWindow(rgba, w, h));

        Array.Fill(rgba, (byte)255);
        Assert.Null(Bezel.FindWindow(rgba, w, h)); // nothing to see through
    }

    [Fact]
    public void A_bezel_picture_loads_with_its_window_or_the_one_its_json_gives()
    {
        var dir = Directory.CreateTempSubdirectory("arcade-bezel-load").FullName;
        try
        {
            const int w = 192, h = 108;
            var rgba = new byte[w * h * 4];
            for (var i = 0; i < rgba.Length; i += 4)
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (100, 50, 200, 255);
            for (var y = 6; y < 102; y++)
                for (var x = 32; x < 160; x++)
                    rgba[(y * w + x) * 4 + 3] = 0;
            var path = Path.Combine(dir, "gridlee.png");
            PngEncoder.Save(new Rgba32Image(w, h, rgba), path);

            var bezel = Bezel.Load(path);
            Assert.Equal((192, 108), (bezel.Width, bezel.Height));
            Assert.Equal(new RectF(32, 6, 128, 96), bezel.Window);
            Assert.Equal(w * h * 4, bezel.Pixels.Length);

            File.WriteAllText(Path.ChangeExtension(path, ".json"), """{ "x": 40, "y": 10, "width": 100, "height": 80 }""");
            Assert.Equal(new RectF(40, 10, 100, 80), Bezel.Load(path).Window);

            File.WriteAllText(Path.ChangeExtension(path, ".json"), "{ not json");
            Assert.Throws<InvalidDataException>(() => Bezel.Load(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Placing_a_bezel_scales_its_window_with_it()
    {
        var bezel = new Bezel { Path = "b.png", Width = 1920, Height = 1080, Window = new RectF(240, 0, 1440, 1080), Pixels = [] };
        var (image, window) = bezel.Place(new RectF(0, 0, 1280, 1024));
        Assert.Equal(new RectF(0, 152, 1280, 720), image);
        Assert.Equal(new RectF(160, 152, 960, 720), window);
    }

    [Fact]
    public void Games_find_their_own_bezel_then_their_parents_then_a_default()
    {
        var root = Directory.CreateTempSubdirectory("arcade-bezels").FullName;
        try
        {
            var dir = Path.Combine(root, "bezels");
            Directory.CreateDirectory(dir);
            Assert.Null(Bezel.Find(root, "sf2ce", "sf2", vertical: false));
            File.WriteAllText(Path.Combine(dir, "default-horizontal.png"), "");
            Assert.EndsWith("default-horizontal.png", Bezel.Find(root, "sf2ce", "sf2", vertical: false));
            Assert.Null(Bezel.Find(root, "1942", null, vertical: true));
            File.WriteAllText(Path.Combine(dir, "sf2.png"), "");
            Assert.EndsWith("sf2.png", Bezel.Find(root, "sf2ce", "sf2", vertical: false));
            File.WriteAllText(Path.Combine(dir, "sf2ce.png"), "");
            Assert.EndsWith("sf2ce.png", Bezel.Find(root, "sf2ce", "sf2", vertical: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>Draws test pictures through the real shader chains in a hidden window.</summary>
public sealed class ShaderRenderTests : IDisposable
{
    const int Width = 960, Height = 720;
    readonly AppWindow? _window;
    readonly VideoRenderer? _video;
    readonly ShaderLibrary _library = new(Path.Combine(Path.GetTempPath(), "no-such-folder"));
    readonly List<string> _reports = new();

    public ShaderRenderTests()
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
            return;
        try
        {
            _window = new AppWindow("test", 4.0 / 3, false, (Width, Height));
            _video = new VideoRenderer(_window.Gl, _library, _reports.Add);
        }
        catch (InvalidOperationException)
        {
            _window?.Dispose();
            _window = null;
        }
    }

    public void Dispose()
    {
        _video?.Dispose();
        _window?.Dispose();
    }

    /// <summary>A 320×240 test picture in XRGB8888: colour bars, a white top row and a red left column.</summary>
    static VideoFrame TestFrame(long number = 1, Func<int, int, (byte R, byte G, byte B)>? pixel = null)
    {
        const int w = 320, h = 240;
        pixel ??= (x, y) => y == 0 ? ((byte)255, (byte)255, (byte)255) : x == 0 ? ((byte)255, (byte)0, (byte)0)
            : (x / 40) switch
            {
                0 => ((byte)200, (byte)200, (byte)200), 1 => ((byte)200, (byte)200, (byte)0), 2 => ((byte)0, (byte)200, (byte)200),
                3 => ((byte)0, (byte)200, (byte)0), 4 => ((byte)200, (byte)0, (byte)200), 5 => ((byte)200, (byte)0, (byte)0),
                6 => ((byte)0, (byte)0, (byte)200), _ => ((byte)(y & 255), (byte)(y & 255), (byte)(y & 255)),
            };
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = pixel(x, y);
                var i = (y * w + x) * 4;
                (data[i], data[i + 1], data[i + 2], data[i + 3]) = (b, g, r, 0);
            }
        var frame = new VideoFrame();
        unsafe
        {
            fixed (byte* p = data)
                frame.CopyFrom(p, w, h, w * 4, PixelFormat.Xrgb8888, number);
        }
        return frame;
    }

    Rgba32Image Draw(VideoFrame frame, PictureSettings picture, uint turns = 0, RectF? area = null)
    {
        var gl = _window!.Gl;
        _window.BeginFrame();
        gl.Viewport(0, 0, Width, Height);
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(Silk.NET.OpenGL.ClearBufferMask.ColorBufferBit);
        _video!.Render(frame, turns, turns % 2 == 1 ? 3.0 / 4 : 4.0 / 3, area ?? new RectF(0, 0, Width, Height), Height, picture);
        Assert.Equal(Silk.NET.OpenGL.GLEnum.NoError, gl.GetError());
        return _window.ReadPixels();
    }

    static (byte R, byte G, byte B) At(Rgba32Image image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }

    static readonly string Shots = Path.Combine(Path.GetTempPath(), "arcade-picture-tests");

    [Fact]
    public void Every_built_in_style_draws_the_game()
    {
        if (_window == null) return; // no graphics available
        Directory.CreateDirectory(Shots);
        foreach (var style in ShaderLibrary.BuiltIn)
        {
            var image = Draw(TestFrame(), new PictureSettings { Style = style.Id });
            Assert.Empty(_reports);
            Assert.Equal(style.Id, _video!.ActiveStyle);
            PngEncoder.Save(image, Path.Combine(Shots, style.Id + ".png"));
            // The yellow bar (columns 40–79 of 320) shows yellow in the middle of the screen.
            var (r, g, b) = At(image, (int)(60 / 320.0 * Width), Height / 2);
            Assert.True(r > 120 && g > 120 && b < 60, $"{style.Id}: expected yellow, got {r},{g},{b}");
        }
    }

    [Fact]
    public void Sharp_pixels_at_a_whole_multiple_match_the_game_exactly()
    {
        if (_window == null) return;
        var frame = TestFrame();
        var image = Draw(frame, new PictureSettings { Style = "sharp", Scaling = PictureScaling.Integer, Shape = PictureShape.SquarePixels });
        var source = frame.ToRgba32();
        for (var y = 0; y < Height; y += 7)
            for (var x = 0; x < Width; x += 5)
            {
                var i = (y / 3 * 320 + x / 3) * 4;
                Assert.Equal((source.Pixels[i], source.Pixels[i + 1], source.Pixels[i + 2]), At(image, x, y));
            }
    }

    [Fact]
    public void A_turned_game_has_its_top_row_on_the_left()
    {
        if (_window == null) return;
        // One quarter turn counter-clockwise, as for a vertical game: the white top row ends up at the left.
        var image = Draw(TestFrame(), new PictureSettings { Style = "sharp", Scaling = PictureScaling.Stretch }, turns: 1);
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(image, 1, Height / 2));
        // ...and the red left column at the bottom.
        Assert.Equal(((byte)255, (byte)0, (byte)0), At(image, Width / 2, Height - 2));
    }

    [Fact]
    public void Scanlines_are_darker_between_lines()
    {
        if (_window == null) return;
        var grey = TestFrame(pixel: (_, _) => (180, 180, 180));
        var image = Draw(grey, new PictureSettings { Style = "scanlines", Scaling = PictureScaling.Integer, Shape = PictureShape.SquarePixels });
        // Lines are 3 screen pixels tall; the middle row of a line is brighter than the edge between lines.
        int middle = At(image, 480, 3 * 100 + 1).G, edge = At(image, 480, 3 * 100).G;
        Assert.True(middle > edge + 20, $"middle {middle}, edge {edge}");
    }

    [Fact]
    public void A_style_that_fails_to_compile_falls_back_to_sharp_pixels()
    {
        if (_window == null) return;
        var folder = Directory.CreateTempSubdirectory("arcade-bad-shader").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "broken.glslp"), "shaders = 1\nshader0 = broken.glsl\n");
            File.WriteAllText(Path.Combine(folder, "broken.glsl"), "#if defined(VERTEX)\nvoid main() { nonsense; }\n#endif\n");
            var library = new ShaderLibrary(folder);
            using var video = new VideoRenderer(_window.Gl, library, _reports.Add);
            _window.BeginFrame();
            video.Render(TestFrame(), 0, 4.0 / 3, new RectF(0, 0, Width, Height), Height, new PictureSettings { Style = "broken.glslp" });
            Assert.Equal("sharp", video.ActiveStyle);
            var message = Assert.Single(_reports);
            Assert.Contains("broken.glsl", message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_bezel_frames_the_game_in_its_window()
    {
        if (_window == null) return;
        // A 16:9 bezel, purple with a see-through 4:3 window in the middle.
        const int bw = 320, bh = 180;
        var rgba = new byte[bw * bh * 4];
        for (var i = 0; i < rgba.Length; i += 4)
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (120, 40, 160, 255);
        for (var y = 20; y < 160; y++)
            for (var x = 67; x < 254; x++)
                rgba[(y * bw + x) * 4 + 3] = 0;
        var dir = Directory.CreateTempSubdirectory("arcade-bezel-draw").FullName;
        try
        {
            var path = Path.Combine(dir, "test.png");
            PngEncoder.Save(new Rgba32Image(bw, bh, rgba), path);
            var bezel = Bezel.Load(path);
            using var texture = new Texture(_window.Gl, bezel.Width, bezel.Height, bezel.Pixels);
            using var ui = new UiRenderer(_window.Gl);

            // As the game scene draws it: the game in the window, then the bezel over everything.
            var (image, window) = bezel.Place(new RectF(0, 0, Width, Height));
            _window.BeginFrame();
            _window.Gl.Viewport(0, 0, Width, Height);
            _window.Gl.Clear(Silk.NET.OpenGL.ClearBufferMask.ColorBufferBit);
            var game = _video!.Render(TestFrame(pixel: (_, _) => (0, 200, 0)), 0, 4.0 / 3, window, Height, new PictureSettings());
            ui.Begin(Width, Height);
            ui.Image(texture, image);
            ui.End();
            var shot = _window.ReadPixels();
            Directory.CreateDirectory(Shots);
            PngEncoder.Save(shot, Path.Combine(Shots, "bezel.png"));

            Assert.True(game.W <= window.W + 0.5f && game.H <= window.H + 0.5f);
            Assert.Equal(((byte)0, (byte)200, (byte)0), At(shot, Width / 2, Height / 2)); // the game shows through
            Assert.Equal(((byte)120, (byte)40, (byte)160), At(shot, Width / 2, (int)image.Y + 5)); // the bezel above it
            Assert.Equal(((byte)0, (byte)0, (byte)0), At(shot, Width / 2, 5)); // the bezel keeps its shape: bars above
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Glow_background_fills_the_bars_with_the_games_colours()
    {
        if (_window == null) return;
        var red = TestFrame(pixel: (_, _) => (220, 0, 0));
        // A wide area, so a 4:3 game leaves bars at the sides.
        var wide = new RectF(0, 0, Width, Height / 2);
        var plain = Draw(red, new PictureSettings(), area: wide);
        Assert.Equal(((byte)0, (byte)0, (byte)0), At(plain, 10, Height / 4));
        var glow = Draw(red, new PictureSettings { Background = PictureBackground.Glow }, area: wide);
        var (r, g, b) = At(glow, 10, Height / 4);
        Assert.True(r > 30 && g < 10 && b < 10, $"bar should glow red, got {r},{g},{b}");
        Assert.True(r < 160, "the glow stays dim");
    }
}
