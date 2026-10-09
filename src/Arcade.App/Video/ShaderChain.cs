using System.Text.RegularExpressions;
using Silk.NET.OpenGL;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace Arcade.App.Video;

/// <summary>A texture the chain reads: its GL name and size in pixels.</summary>
readonly record struct ChainTexture(uint Handle, int Width, int Height);

/// <summary>What one frame of the chain draws from and to.</summary>
readonly record struct ChainFrame
{
    /// <summary>The game's picture as the core made it (row 0 at the top), in the core's orientation.</summary>
    public required ChainTexture Source { get; init; }
    /// <summary>Earlier frames, newest first, for shaders that blend frames together.</summary>
    public IReadOnlyList<ChainTexture> History { get; init; }
    public long FrameCount { get; init; }
    /// <summary>1 normally, -1 while rewinding.</summary>
    public int FrameDirection { get; init; }
    /// <summary>Where the game goes on the target, in GL pixels (bottom-left origin).</summary>
    public required (int X, int Y, int W, int H) Viewport { get; init; }
    /// <summary>Quarter turns counter-clockwise to show the picture at.</summary>
    public int Turns { get; init; }
    public required float Alpha { get; init; }
    public uint TargetFramebuffer { get; init; }
    /// <summary>Parameter values chosen by the player, by name; anything missing uses the preset's value or the shader's default.</summary>
    public IReadOnlyDictionary<string, float>? Parameters { get; init; }
}

/// <summary>
/// Runs a <see cref="ShaderPreset"/> on the GPU: each pass draws its input into a texture sized as
/// the preset says, and the last pass draws onto the screen, turned to the game's orientation. The
/// passes work in the game's own orientation, so scanlines follow the game's raster lines, as on a
/// real monitor turned on its side.
/// </summary>
sealed unsafe partial class ShaderChain : IDisposable
{
    const string StockShader = """
        #version 330
        #if defined(VERTEX)
        in vec4 VertexCoord;
        in vec4 TexCoord;
        out vec2 vTex;
        uniform mat4 MVPMatrix;
        void main() { gl_Position = MVPMatrix * VertexCoord; vTex = TexCoord.xy; }
        #elif defined(FRAGMENT)
        in vec2 vTex;
        uniform sampler2D Texture;
        out vec4 FragColor;
        void main() { FragColor = vec4(texture(Texture, vTex).rgb, 1.0); }
        #endif
        """;

    enum UniformKind { Float, Int, Vec2, Mat4, Sampler, Other }

    readonly record struct Uniform(int Location, UniformKind Kind);

    sealed class Pass
    {
        public required ShaderPass Config;
        public required ShaderSource Source;
        public uint Program, Vao;
        public Dictionary<string, Uniform> Uniforms = new(StringComparer.Ordinal);
        // Output texture, when the pass draws into one.
        public uint Fbo, Texture;
        public int Width, Height;
        public InternalFormat Format;
    }

    readonly GL _gl;
    readonly List<Pass> _passes = new();
    readonly Dictionary<string, (uint Handle, int W, int H, PresetTexture Info)> _luts = new(StringComparer.Ordinal);
    readonly Dictionary<(bool Linear, WrapMode Wrap, bool Mipmap), uint> _samplers = new();
    readonly uint _vbo;
    readonly bool _compatibility;
    readonly ShaderPreset _preset;
    int _unitsUsed;

    /// <summary>Every adjustable setting across the passes, in order.</summary>
    public IReadOnlyList<ShaderParameter> Parameters { get; }
    /// <summary>How many earlier frames the shaders read.</summary>
    public int HistoryNeeded { get; }
    public int PassCount => _passes.Count;

    /// <param name="readText">Reads a shader file by the path written in the preset.</param>
    /// <param name="readBytes">Reads a look-up texture's file.</param>
    /// <exception cref="ShaderException">A shader didn't compile or a file is missing.</exception>
    public ShaderChain(GL gl, ShaderPreset preset, Func<string, string> readText, Func<string, byte[]> readBytes)
    {
        _gl = gl;
        _preset = preset;
        gl.GetInteger(GLEnum.ContextProfileMask, out int profile);
        _compatibility = (profile & (int)GLEnum.ContextCompatibilityProfileBit) != 0;
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        // Per corner: VertexCoord (x, y, 0, 1), TexCoord (u, v, 0, 0), COLOR (1, 1, 1, 1).
        ReadOnlySpan<float> quad =
        [
            0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 1, 1,
            1, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 1,
            0, 1, 0, 1, 0, 1, 0, 0, 1, 1, 1, 1,
            1, 1, 0, 1, 1, 1, 0, 0, 1, 1, 1, 1,
        ];
        gl.BufferData(BufferTargetARB.ArrayBuffer, quad, BufferUsageARB.StaticDraw);

        try
        {
            var passes = preset.Passes.ToList();
            if (!passes[^1].IsViewportSized)
            {
                // The last pass draws into a texture of its own size; a plain pass stretches that onto the screen.
                passes.Add(new ShaderPass { Path = "stock", ScaleTypeX = ScaleType.Viewport, ScaleTypeY = ScaleType.Viewport, FilterLinear = true });
            }
            foreach (var config in passes)
            {
                var source = new ShaderSource(config.Path, config.Path == "stock" ? StockShader : ReadOrThrow(readText, config.Path));
                var pass = new Pass { Config = config, Source = source };
                pass.Program = Link(source);
                ReadUniforms(pass);
                pass.Vao = MakeVao(pass.Program);
                _passes.Add(pass);
            }
            foreach (var texture in preset.Textures)
                _luts[texture.Name] = LoadLut(texture, readBytes);
        }
        catch
        {
            Dispose();
            throw;
        }

        var parameters = new List<ShaderParameter>();
        foreach (var pass in _passes)
            foreach (var p in pass.Source.Parameters)
                if (parameters.All(q => q.Name != p.Name))
                    parameters.Add(preset.Parameters.TryGetValue(p.Name, out var value) ? p with { Default = value } : p);
        Parameters = parameters;
        HistoryNeeded = _passes.Max(p => HistoryDepth(p.Source.Text));
    }

    static string ReadOrThrow(Func<string, string> readText, string path)
    {
        try
        {
            return readText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ShaderException($"Can't read {Path.GetFileName(path)}: {e.Message}");
        }
    }

    [GeneratedRegex(@"\bPrev(\d?)Texture\b")]
    private static partial Regex PrevPattern();

    /// <summary>1 + the furthest frame back the shader reads (PrevTexture = 1 back, Prev6Texture = 7).</summary>
    static int HistoryDepth(string text)
    {
        var depth = 0;
        foreach (Match m in PrevPattern().Matches(text))
        {
            if (m.Index >= 4 && text.AsSpan(m.Index - 4, 4).SequenceEqual("Pass"))
                continue; // PassPrevNTexture is another pass, not an earlier frame
            depth = Math.Max(depth, m.Groups[1].Value.Length == 0 ? 1 : int.Parse(m.Groups[1].Value) + 1);
        }
        return depth;
    }

    /// <summary>For diagnosing presets: each pass's settings and whether its last output was a single flat colour.</summary>
    public List<string> Describe()
    {
        var lines = new List<string>();
        for (var i = 0; i < _passes.Count; i++)
        {
            var pass = _passes[i];
            var c = pass.Config;
            var flat = "screen";
            if (pass.Texture != 0)
            {
                var pixels = new float[pass.Width * pass.Height * 4];
                _gl.BindTexture(TextureTarget.Texture2D, pass.Texture);
                fixed (float* p = pixels)
                    _gl.GetTexImage(TextureTarget.Texture2D, 0, GlPixelFormat.Rgba, PixelType.Float, p);
                var first = (pixels[0], pixels[1], pixels[2], pixels[3]);
                var uniform = true;
                for (var k = 4; k < pixels.Length && uniform; k += 4)
                    uniform = Math.Abs(pixels[k] - first.Item1) < 1e-4 && Math.Abs(pixels[k + 1] - first.Item2) < 1e-4 && Math.Abs(pixels[k + 2] - first.Item3) < 1e-4;
                flat = uniform ? $"FLAT {first.Item1:F2},{first.Item2:F2},{first.Item3:F2},{first.Item4:F2}" : "varied";
            }
            var used = string.Join(",", pass.Uniforms.Where(u => u.Value.Kind == UniformKind.Sampler).Select(u => u.Key));
            _gl.GetProgram(pass.Program, ProgramPropertyARB.ActiveAttributes, out var attributeCount);
            var attributes = string.Join(",", Enumerable.Range(0, attributeCount).Select(a => _gl.GetActiveAttrib(pass.Program, (uint)a, out _, out _)));
            used += $"] attribs=[{attributes}] uniforms=[{string.Join(",", pass.Uniforms.Where(u => u.Value.Kind != UniformKind.Sampler).Select(u => u.Key))}";
            lines.Add($"  pass {i} {Path.GetFileName(c.Path)} {pass.Width}x{pass.Height} {c.ScaleTypeX}/{c.ScaleTypeY} float={c.FloatFramebuffer} srgb={c.SrgbFramebuffer} mip={c.MipmapInput} wrap={c.Wrap} alias={c.Alias} linear={c.FilterLinear} samplers=[{used}] -> {flat}");
        }
        return lines;
    }

    // ---- Drawing ----

    public void Render(in ChainFrame frame)
    {
        var (vpW, vpH) = frame.Turns % 2 == 1 ? (frame.Viewport.H, frame.Viewport.W) : (frame.Viewport.W, frame.Viewport.H);
        var input = frame.Source;
        var original = frame.Source;
        Span<float> mvp = stackalloc float[16];

        for (var i = 0; i < _passes.Count; i++)
        {
            var pass = _passes[i];
            var config = pass.Config;
            var last = i == _passes.Count - 1;

            if (config.MipmapInput)
            {
                _gl.BindTexture(TextureTarget.Texture2D, input.Handle);
                _gl.GenerateMipmap(TextureTarget.Texture2D);
            }

            int outW, outH;
            if (last)
            {
                (outW, outH) = (vpW, vpH);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, frame.TargetFramebuffer);
                _gl.Viewport(frame.Viewport.X, frame.Viewport.Y, (uint)frame.Viewport.W, (uint)frame.Viewport.H);
                ScreenMatrix(mvp, frame.Turns);
                _gl.Disable(EnableCap.FramebufferSrgb);
                if (frame.Alpha < 1)
                {
                    _gl.Enable(EnableCap.Blend);
                    _gl.BlendColor(0, 0, 0, frame.Alpha);
                    _gl.BlendFunc(BlendingFactor.ConstantAlpha, BlendingFactor.OneMinusConstantAlpha);
                }
                else
                {
                    _gl.Disable(EnableCap.Blend);
                }
            }
            else
            {
                outW = Size(config.ScaleTypeX, config.ScaleX, input.Width, vpW);
                outH = Size(config.ScaleTypeY, config.ScaleY, input.Height, vpH);
                EnsureTarget(pass, outW, outH);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, pass.Fbo);
                _gl.Viewport(0, 0, (uint)outW, (uint)outH);
                TextureMatrix(mvp);
                if (config.SrgbFramebuffer)
                    _gl.Enable(EnableCap.FramebufferSrgb);
                else
                    _gl.Disable(EnableCap.FramebufferSrgb);
                _gl.Disable(EnableCap.Blend);
            }

            _gl.UseProgram(pass.Program);
            _unitsUsed = 0;
            SetMatrix(pass, "MVPMatrix", mvp);
            var count = config.FrameCountMod > 0 ? frame.FrameCount % config.FrameCountMod : frame.FrameCount;
            SetInt(pass, "FrameCount", (int)count);
            SetInt(pass, "FrameDirection", frame.FrameDirection);
            SetVec2(pass, "OutputSize", outW, outH);
            BindTexture(pass, "Texture", input, SamplerFor(i), "TextureSize", "InputSize");
            BindTexture(pass, "OrigTexture", original, SamplerFor(0), "OrigTextureSize", "OrigInputSize");
            // Counting back past the first pass reaches the game's own picture.
            BindTexture(pass, $"PassPrev{i + 1}Texture", original, SamplerFor(0), $"PassPrev{i + 1}TextureSize", $"PassPrev{i + 1}InputSize");

            // Earlier passes: Pass1 is the first pass's output; PassPrev1 is this pass's input, PassPrev2 the one before.
            for (var k = 0; k < i; k++)
            {
                var earlier = _passes[k];
                var texture = new ChainTexture(earlier.Texture, earlier.Width, earlier.Height);
                var sampler = SamplerFor(k + 1);
                BindTexture(pass, $"Pass{k + 1}Texture", texture, sampler, $"Pass{k + 1}TextureSize", $"Pass{k + 1}InputSize");
                BindTexture(pass, $"PassPrev{i - k}Texture", texture, sampler, $"PassPrev{i - k}TextureSize", $"PassPrev{i - k}InputSize");
                if (earlier.Config.Alias is { } alias)
                    BindTexture(pass, alias + "Texture", texture, sampler, alias + "TextureSize", alias + "InputSize");
            }

            // Earlier frames; without enough history yet, the current frame stands in.
            if (HistoryNeeded > 0)
                for (var k = 0; k < HistoryNeeded; k++)
                {
                    var past = frame.History is { Count: > 0 } h ? h[Math.Min(k, h.Count - 1)] : original;
                    var name = k == 0 ? "Prev" : $"Prev{k}";
                    BindTexture(pass, name + "Texture", past, SamplerFor(0), name + "TextureSize", name + "InputSize");
                }

            foreach (var (name, lut) in _luts)
                BindTexture(pass, name, new ChainTexture(lut.Handle, lut.W, lut.H), Sampler(lut.Info.Linear, lut.Info.Wrap, lut.Info.Mipmap), name + "Size", null);

            foreach (var parameter in pass.Source.Parameters)
            {
                var value = frame.Parameters != null && frame.Parameters.TryGetValue(parameter.Name, out var chosen) ? chosen
                    : _preset.Parameters.TryGetValue(parameter.Name, out var preset) ? preset
                    : parameter.Default;
                SetFloat(pass, parameter.Name, value);
            }

            _gl.BindVertexArray(pass.Vao);
            _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            if (!last)
                input = new ChainTexture(pass.Texture, outW, outH);
        }

        // Leave GL as the UI renderer expects it.
        for (var unit = 0; unit < Math.Max(_unitsUsed, 1); unit++)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindSampler((uint)unit, 0);
        }
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.Disable(EnableCap.FramebufferSrgb);
        _gl.Disable(EnableCap.Blend);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, frame.TargetFramebuffer);
    }

    static int Size(ScaleType type, float scale, int input, int viewport)
    {
        var size = type switch
        {
            ScaleType.Viewport => viewport * scale,
            ScaleType.Absolute => scale,
            _ => input * scale,
        };
        return Math.Clamp((int)MathF.Round(size), 1, 8192);
    }

    /// <summary>Maps the 0..1 quad onto a texture target: (0,0) at the bottom-left, which holds the picture's top row.</summary>
    static void TextureMatrix(Span<float> m)
    {
        m.Clear();
        m[0] = 2; m[5] = 2; m[10] = 1; m[12] = -1; m[13] = -1; m[15] = 1;
    }

    /// <summary>Maps the 0..1 quad onto the screen with the picture's top row at the top, turned counter-clockwise.</summary>
    static void ScreenMatrix(Span<float> m, int turns)
    {
        // Upright: x' = 2x - 1, y' = 1 - 2y. Then rotate by turns × 90° counter-clockwise.
        var (c, s) = ((turns % 4 + 4) % 4) switch { 1 => (0, 1), 2 => (-1, 0), 3 => (0, -1), _ => (1, 0) };
        float ax = 2, ay = 0, bx = 0, by = -2, tx = -1, ty = 1; // rows of the upright mapping: (ax, bx | tx), (ay, by | ty)
        // Rotated rows: row1 = c·r1 − s·r2, row2 = s·r1 + c·r2.
        var r1 = (X: c * ax - s * ay, Y: c * bx - s * by, T: c * tx - s * ty);
        var r2 = (X: s * ax + c * ay, Y: s * bx + c * by, T: s * tx + c * ty);
        m.Clear();
        // Column-major.
        m[0] = r1.X; m[1] = r2.X;
        m[4] = r1.Y; m[5] = r2.Y;
        m[10] = 1;
        m[12] = r1.T; m[13] = r2.T; m[15] = 1;
    }

    // ---- Textures and samplers ----

    /// <summary>The sampler pass <paramref name="reader"/> reads its input with (the input belongs to the pass before it).</summary>
    uint SamplerFor(int reader)
    {
        if (reader >= _passes.Count)
            return Sampler(true, WrapMode.ClampToEdge, false);
        var config = _passes[reader].Config;
        return Sampler(config.FilterLinear ?? true, config.Wrap, config.MipmapInput);
    }

    uint Sampler(bool linear, WrapMode wrap, bool mipmap)
    {
        if (_samplers.TryGetValue((linear, wrap, mipmap), out var sampler))
            return sampler;
        sampler = _gl.GenSampler();
        var min = mipmap ? (linear ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.NearestMipmapNearest) : linear ? TextureMinFilter.Linear : TextureMinFilter.Nearest;
        _gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)min);
        _gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)(linear ? TextureMagFilter.Linear : TextureMagFilter.Nearest));
        var glWrap = (int)(wrap switch
        {
            WrapMode.ClampToEdge => TextureWrapMode.ClampToEdge,
            WrapMode.Repeat => TextureWrapMode.Repeat,
            WrapMode.MirroredRepeat => TextureWrapMode.MirroredRepeat,
            _ => TextureWrapMode.ClampToBorder,
        });
        _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, glWrap);
        _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, glWrap);
        ReadOnlySpan<float> black = [0, 0, 0, 0];
        fixed (float* b = black)
            _gl.SamplerParameter(sampler, SamplerParameterF.BorderColor, b);
        _samplers[(linear, wrap, mipmap)] = sampler;
        return sampler;
    }

    void BindTexture(Pass pass, string name, ChainTexture texture, uint sampler, string sizeName, string? inputSizeName)
    {
        if (pass.Uniforms.TryGetValue(name, out var u) && u.Kind == UniformKind.Sampler)
        {
            var unit = _unitsUsed++;
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindTexture(TextureTarget.Texture2D, texture.Handle);
            _gl.BindSampler((uint)unit, sampler);
            _gl.Uniform1(u.Location, unit);
        }
        SetVec2(pass, sizeName, texture.Width, texture.Height);
        if (inputSizeName != null)
            SetVec2(pass, inputSizeName, texture.Width, texture.Height);
    }

    void EnsureTarget(Pass pass, int width, int height)
    {
        var format = pass.Config.FloatFramebuffer ? InternalFormat.Rgba32f : pass.Config.SrgbFramebuffer ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8;
        if (pass.Texture != 0 && pass.Width == width && pass.Height == height && pass.Format == format)
            return;
        if (pass.Texture == 0)
        {
            pass.Texture = _gl.GenTexture();
            pass.Fbo = _gl.GenFramebuffer();
        }
        _gl.BindTexture(TextureTarget.Texture2D, pass.Texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, format, (uint)width, (uint)height, 0, GlPixelFormat.Rgba,
            pass.Config.FloatFramebuffer ? PixelType.Float : PixelType.UnsignedByte, null);
        // Filtering comes from samplers; these only matter if a texture is read without one.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, pass.Fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, pass.Texture, 0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new ShaderException($"The graphics driver can't draw into a {format} texture ({status})");
        (pass.Width, pass.Height, pass.Format) = (width, height, format);
    }

    (uint Handle, int W, int H, PresetTexture Info) LoadLut(PresetTexture info, Func<string, byte[]> readBytes)
    {
        StbImageSharp.ImageResult image;
        try
        {
            image = StbImageSharp.ImageResult.FromMemory(readBytes(info.Path), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            throw new ShaderException($"Can't load the texture {Path.GetFileName(info.Path)}: {e.Message}");
        }
        var handle = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, handle);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = image.Data)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)image.Width, (uint)image.Height, 0, GlPixelFormat.Rgba, PixelType.UnsignedByte, p);
        if (info.Mipmap)
            _gl.GenerateMipmap(TextureTarget.Texture2D);
        return (handle, image.Width, image.Height, info);
    }

    // ---- Programs and uniforms ----

    uint Link(ShaderSource source)
    {
        var vs = Compile(ShaderType.VertexShader, source, vertex: true);
        uint fs;
        try
        {
            fs = Compile(ShaderType.FragmentShader, source, vertex: false);
        }
        catch
        {
            _gl.DeleteShader(vs);
            throw;
        }
        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0)
        {
            var log = _gl.GetProgramInfoLog(program);
            _gl.DeleteProgram(program);
            throw new ShaderException($"{Path.GetFileName(source.Path)} didn't link: {FirstLines(log)}");
        }
        return program;
    }

    uint Compile(ShaderType type, ShaderSource source, bool vertex)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source.Stage(vertex, _compatibility));
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0)
        {
            var log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new ShaderException($"{Path.GetFileName(source.Path)} ({(vertex ? "vertex" : "fragment")}) didn't compile: {FirstLines(log)}");
        }
        return shader;
    }

    static string FirstLines(string log)
    {
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" / ", lines.Take(3));
    }

    void ReadUniforms(Pass pass)
    {
        _gl.GetProgram(pass.Program, ProgramPropertyARB.ActiveUniforms, out var count);
        for (uint i = 0; i < count; i++)
        {
            var name = _gl.GetActiveUniform(pass.Program, i, out _, out UniformType type);
            if (name.EndsWith("[0]", StringComparison.Ordinal))
                name = name[..^3];
            var kind = type switch
            {
                UniformType.Float => UniformKind.Float,
                UniformType.Int => UniformKind.Int,
                UniformType.FloatVec2 => UniformKind.Vec2,
                UniformType.FloatMat4 => UniformKind.Mat4,
                UniformType.Sampler2D => UniformKind.Sampler,
                _ => UniformKind.Other,
            };
            pass.Uniforms[name] = new Uniform(_gl.GetUniformLocation(pass.Program, name), kind);
        }
    }

    /// <summary>A vertex array feeding the program's attributes from the shared quad, by the names the format uses.</summary>
    uint MakeVao(uint program)
    {
        var vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.GetProgram(program, ProgramPropertyARB.ActiveAttributes, out var count);
        const uint stride = 12 * sizeof(float);
        for (uint i = 0; i < count; i++)
        {
            var name = _gl.GetActiveAttrib(program, i, out _, out _);
            var location = _gl.GetAttribLocation(program, name);
            if (location < 0)
                continue;
            // VertexCoord, then TexCoord (and the older per-texture names like OrigTexCoord), then COLOR.
            var offset = name == "VertexCoord" ? 0 : name.EndsWith("TexCoord", StringComparison.Ordinal) ? 4 : name == "COLOR" ? 8 : -1;
            if (offset < 0)
                continue;
            _gl.VertexAttribPointer((uint)location, 4, VertexAttribPointerType.Float, false, stride, (void*)(offset * sizeof(float)));
            _gl.EnableVertexAttribArray((uint)location);
        }
        _gl.BindVertexArray(0);
        return vao;
    }

    void SetFloat(Pass pass, string name, float value)
    {
        if (!pass.Uniforms.TryGetValue(name, out var u))
            return;
        if (u.Kind == UniformKind.Float) _gl.Uniform1(u.Location, value);
        else if (u.Kind == UniformKind.Int) _gl.Uniform1(u.Location, (int)value);
    }

    void SetInt(Pass pass, string name, int value)
    {
        if (!pass.Uniforms.TryGetValue(name, out var u))
            return;
        if (u.Kind == UniformKind.Int) _gl.Uniform1(u.Location, value);
        else if (u.Kind == UniformKind.Float) _gl.Uniform1(u.Location, (float)value);
    }

    void SetVec2(Pass pass, string name, float x, float y)
    {
        if (pass.Uniforms.TryGetValue(name, out var u) && u.Kind == UniformKind.Vec2)
            _gl.Uniform2(u.Location, x, y);
    }

    void SetMatrix(Pass pass, string name, ReadOnlySpan<float> m)
    {
        if (pass.Uniforms.TryGetValue(name, out var u) && u.Kind == UniformKind.Mat4)
            fixed (float* p = m)
                _gl.UniformMatrix4(u.Location, 1, false, p);
    }

    public void Dispose()
    {
        foreach (var pass in _passes)
        {
            if (pass.Program != 0) _gl.DeleteProgram(pass.Program);
            if (pass.Vao != 0) _gl.DeleteVertexArray(pass.Vao);
            if (pass.Texture != 0) _gl.DeleteTexture(pass.Texture);
            if (pass.Fbo != 0) _gl.DeleteFramebuffer(pass.Fbo);
        }
        _passes.Clear();
        foreach (var lut in _luts.Values)
            _gl.DeleteTexture(lut.Handle);
        _luts.Clear();
        foreach (var sampler in _samplers.Values)
            _gl.DeleteSampler(sampler);
        _samplers.Clear();
        _gl.DeleteBuffer(_vbo);
    }
}
