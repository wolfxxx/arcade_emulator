using Arcade.App.Video;
using Arcade.Libretro;
using Silk.NET.OpenGL;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;
using RetroPixelFormat = Arcade.Libretro.PixelFormat;

namespace Arcade.App;

/// <summary>
/// Draws the core's framebuffer: uploads it to a texture in its native pixel format (no CPU
/// conversion), places it on screen by the picture settings, and runs it through the chosen
/// picture style's shader chain, which also turns it to the game's orientation. Optionally fills
/// the space around it with a soft glow of the game's colours.
/// </summary>
sealed unsafe class VideoRenderer : IDisposable
{
    const string GlowVertex = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        uniform int uRotation;   // quarter turns counter-clockwise
        uniform vec2 uCover;     // share of the picture visible across and down
        out vec2 vUv;
        void main()
        {
            vec2 s = vec2(aPos.x * 0.5 + 0.5, 0.5 - aPos.y * 0.5); // screen position, top-left origin
            vec2 uv = 0.5 + (s - 0.5) * uCover;
            if (uRotation == 1) uv = vec2(1.0 - uv.y, uv.x);
            else if (uRotation == 2) uv = vec2(1.0 - uv.x, 1.0 - uv.y);
            else if (uRotation == 3) uv = vec2(uv.y, 1.0 - uv.x);
            vUv = uv;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    const string GlowFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uTex;
        uniform float uLod;
        uniform vec2 uStep;
        out vec4 fragColor;
        void main()
        {
            // A wide, soft blur from a small mipmap level, kept dim so it doesn't compete with the game.
            vec3 sum = vec3(0.0);
            float total = 0.0;
            for (int y = -3; y <= 3; y++)
                for (int x = -3; x <= 3; x++)
                {
                    float w = exp(-0.25 * float(x * x + y * y));
                    sum += textureLod(uTex, vUv + vec2(x, y) * uStep, uLod).rgb * w;
                    total += w;
                }
            vec2 edge = abs(vUv - 0.5) * 2.0;
            float fade = 1.0 - 0.35 * dot(edge, edge);
            fragColor = vec4(sum / total * 0.42 * fade, 1.0);
        }
        """;

    readonly GL _gl;
    readonly ShaderLibrary _library;
    readonly Action<string> _report;
    readonly uint _glowProgram, _glowVao, _glowVbo, _glowSampler;
    readonly int _uRotation, _uCover, _uLod, _uStep;

    // The game's frames: the newest is uploaded into the next texture of the ring, so shaders that
    // blend earlier frames can read them.
    uint[] _frames = [];
    int _head;
    int _texWidth, _texHeight;
    RetroPixelFormat _texFormat;
    long _uploadedFrame = -1;
    bool _mipmapped;
    readonly List<ChainTexture> _history = new();

    ShaderChain? _chain;
    string? _chainStyle;
    readonly HashSet<string> _failedStyles = new(StringComparer.OrdinalIgnoreCase);

    public VideoRenderer(GL gl, ShaderLibrary library, Action<string> report)
    {
        _gl = gl;
        _library = library;
        _report = report;
        _glowProgram = CreateProgram(GlowVertex, GlowFragment);
        _uRotation = _gl.GetUniformLocation(_glowProgram, "uRotation");
        _uCover = _gl.GetUniformLocation(_glowProgram, "uCover");
        _uLod = _gl.GetUniformLocation(_glowProgram, "uLod");
        _uStep = _gl.GetUniformLocation(_glowProgram, "uStep");

        _glowVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_glowVao);
        _glowVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _glowVbo);
        ReadOnlySpan<float> quad = [-1, -1, 1, -1, -1, 1, 1, 1];
        _gl.BufferData(BufferTargetARB.ArrayBuffer, quad, BufferUsageARB.StaticDraw);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 0, null);
        _gl.EnableVertexAttribArray(0);
        _gl.BindVertexArray(0);

        _glowSampler = _gl.GenSampler();
        _gl.SamplerParameter(_glowSampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        _gl.SamplerParameter(_glowSampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
        _gl.SamplerParameter(_glowSampler, SamplerParameterI.WrapS, (int)TextureWrapMode.MirroredRepeat);
        _gl.SamplerParameter(_glowSampler, SamplerParameterI.WrapT, (int)TextureWrapMode.MirroredRepeat);
    }

    /// <summary>The settings the current style offers, with the preset's values as defaults.</summary>
    public IReadOnlyList<ShaderParameter> Parameters => _chain?.Parameters ?? [];

    /// <summary>The settings a style offers (loading it if it isn't the current one).</summary>
    public IReadOnlyList<ShaderParameter> ParametersFor(string style) => Chain(style)?.Parameters ?? [];

    /// <summary>Diagnostics for the current chain (see <see cref="ShaderChain.Describe"/>).</summary>
    public IEnumerable<string> DescribeChain() => _chain?.Describe() ?? [];

    /// <summary>The style actually in use (a style that failed falls back to the default).</summary>
    public string? ActiveStyle => _chainStyle;

    /// <summary>Draws the game inside <paramref name="area"/> (window pixels, top-left origin) as the picture settings say.</summary>
    /// <param name="turns">Quarter turns counter-clockwise to show the core's picture at.</param>
    /// <param name="aspect">Shape of the picture on its original monitor, after turning; 0 means square pixels.</param>
    /// <param name="frameDirection">-1 while rewinding, for shaders that care.</param>
    /// <returns>The rectangle the game image occupies.</returns>
    public Ui.RectF Render(VideoFrame frame, uint turns, double aspect, Ui.RectF area, int windowHeight, PictureSettings picture, int frameDirection = 1, float alpha = 1)
    {
        if (frame.Width == 0 || area.W < 1 || area.H < 1)
            return area;

        var chain = Chain(picture.Style);
        Upload(frame, chain?.HistoryNeeded ?? 0, picture.Background == PictureBackground.Glow);
        var rect = PictureLayout.Place(area, frame.Width, frame.Height, turns % 2 == 1, aspect, picture.Scaling, picture.Shape);
        _gl.GetInteger(GetPName.DrawFramebufferBinding, out int target);

        if (picture.Background == PictureBackground.Glow && alpha >= 1)
            DrawGlow(turns, rect, area, windowHeight);
        if (chain == null)
            return rect;

        var source = new ChainTexture(_frames[_head], _texWidth, _texHeight);
        _history.Clear();
        for (var k = 1; k < _frames.Length; k++)
            _history.Add(new ChainTexture(_frames[(_head - k + _frames.Length) % _frames.Length], _texWidth, _texHeight));
        chain.Render(new ChainFrame
        {
            Source = source,
            History = _history,
            FrameCount = Math.Max(frame.FrameNumber, 0),
            FrameDirection = frameDirection,
            Viewport = ((int)rect.X, windowHeight - (int)rect.Y - (int)rect.H, (int)rect.W, (int)rect.H),
            Turns = (int)(turns % 4),
            Alpha = alpha,
            TargetFramebuffer = (uint)target,
            Parameters = picture.Parameters.TryGetValue(_chainStyle ?? "", out var values) ? values : null,
        });
        return rect;
    }

    /// <summary>The chain for a style, built on first use. A style that fails is reported once and the default is used instead.</summary>
    ShaderChain? Chain(string style)
    {
        if (_failedStyles.Contains(style) || _library.Find(style) == null)
            style = ShaderLibrary.DefaultId;
        if (_chain != null && _chainStyle == style)
            return _chain;
        _chain?.Dispose();
        _chain = null;
        _chainStyle = null;
        try
        {
            _chain = new ShaderChain(_gl, _library.Load(style), _library.ReadText, _library.ReadBytes);
            _chainStyle = style;
        }
        catch (ShaderException e)
        {
            Console.Error.WriteLine($"Picture style {style}: {e.Message}");
            _failedStyles.Add(style);
            if (style == ShaderLibrary.DefaultId)
                return null; // nothing to fall back to; the picture stays black rather than crash
            _report($"Can't use that picture style — {e.Message}");
            return Chain(ShaderLibrary.DefaultId);
        }
        return _chain;
    }

    /// <summary>Forgets styles that failed, so they're tried again (e.g. after a shader file was fixed).</summary>
    public void RetryFailedStyles()
    {
        _failedStyles.Clear();
        _library.Refresh();
    }

    void DrawGlow(uint turns, Ui.RectF picture, Ui.RectF area, int windowHeight)
    {
        // Cover the whole area with the picture, cropping what doesn't fit.
        var pictureAspect = picture.W / picture.H;
        var areaAspect = area.W / area.H;
        var cover = areaAspect > pictureAspect ? (X: 1f, Y: pictureAspect / areaAspect) : (X: areaAspect / pictureAspect, Y: 1f);

        _gl.Viewport((int)area.X, windowHeight - (int)area.Y - (int)area.H, (uint)area.W, (uint)area.H);
        _gl.Disable(EnableCap.Blend);
        _gl.UseProgram(_glowProgram);
        _gl.Uniform1(_uRotation, (int)(turns % 4));
        _gl.Uniform2(_uCover, cover.X, cover.Y);
        var lod = MathF.Max(MathF.Log2(Math.Max(_texWidth, _texHeight) / 24f), 0);
        _gl.Uniform1(_uLod, lod);
        _gl.Uniform2(_uStep, 1.6f / 24, 1.6f / 24 * _texWidth / _texHeight);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _frames[_head]);
        _gl.BindSampler(0, _glowSampler);
        _gl.BindVertexArray(_glowVao);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        _gl.BindSampler(0, 0);
    }

    void Upload(VideoFrame frame, int history, bool mipmaps)
    {
        // One texture per frame kept, plus the current one.
        var wanted = history + 1;
        var resized = frame.Width != _texWidth || frame.Height != _texHeight || frame.Format != _texFormat;
        if (_frames.Length != wanted)
        {
            if (_frames.Length > 0)
                _gl.DeleteTextures(_frames);
            _frames = new uint[wanted];
            for (var i = 0; i < wanted; i++)
                _frames[i] = _gl.GenTexture();
            _head = 0;
            resized = true;
            _texWidth = 0;
        }
        else if (frame.FrameNumber == _uploadedFrame && !resized)
        {
            if (mipmaps && !_mipmapped)
            {
                _gl.BindTexture(TextureTarget.Texture2D, _frames[_head]);
                _gl.GenerateMipmap(TextureTarget.Texture2D);
                _mipmapped = true;
            }
            return;
        }

        var (internalFormat, format, type) = frame.Format switch
        {
            RetroPixelFormat.Xrgb8888 => (InternalFormat.Rgb8, GlPixelFormat.Bgra, PixelType.UnsignedByte),
            RetroPixelFormat.Rgb565 => (InternalFormat.Rgb8, GlPixelFormat.Rgb, PixelType.UnsignedShort565),
            _ => (InternalFormat.Rgb8, GlPixelFormat.Bgra, PixelType.UnsignedShort1555Rev), // 0RGB1555; top bit ignored
        };

        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, frame.Pitch / frame.BytesPerPixel);
        if (resized)
        {
            // Every texture in the ring starts as this frame, so history is never garbage.
            foreach (var texture in _frames)
                Allocate(texture, frame, internalFormat, format, type);
            (_texWidth, _texHeight, _texFormat) = (frame.Width, frame.Height, frame.Format);
            _head = 0;
        }
        else
        {
            _head = (_head + 1) % _frames.Length;
            _gl.BindTexture(TextureTarget.Texture2D, _frames[_head]);
            fixed (byte* pixels = frame.Data)
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)frame.Width, (uint)frame.Height, format, type, pixels);
        }
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        if (mipmaps)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _frames[_head]);
            _gl.GenerateMipmap(TextureTarget.Texture2D);
        }
        _mipmapped = mipmaps;
        _uploadedFrame = frame.FrameNumber;
    }

    void Allocate(uint texture, VideoFrame frame, InternalFormat internalFormat, GlPixelFormat format, PixelType type)
    {
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        // Filtering comes from each pass's sampler; these apply only to plain reads.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        fixed (byte* pixels = frame.Data)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, (uint)frame.Width, (uint)frame.Height, 0, format, type, pixels);
    }

    uint CreateProgram(string vertex, string fragment)
    {
        var vs = Compile(ShaderType.VertexShader, vertex);
        var fs = Compile(ShaderType.FragmentShader, fragment);
        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0)
            throw new InvalidOperationException("Shader link failed: " + _gl.GetProgramInfoLog(program));
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        _gl.UseProgram(program);
        _gl.Uniform1(_gl.GetUniformLocation(program, "uTex"), 0);
        return program;
    }

    uint Compile(ShaderType type, string source)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0)
            throw new InvalidOperationException($"{type} compile failed: {_gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void Dispose()
    {
        _chain?.Dispose();
        if (_frames.Length > 0)
            _gl.DeleteTextures(_frames);
        _gl.DeleteSampler(_glowSampler);
        _gl.DeleteBuffer(_glowVbo);
        _gl.DeleteVertexArray(_glowVao);
        _gl.DeleteProgram(_glowProgram);
    }
}
