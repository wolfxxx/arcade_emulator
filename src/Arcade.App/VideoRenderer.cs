using Arcade.Libretro;
using Silk.NET.OpenGL;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;
using RetroPixelFormat = Arcade.Libretro.PixelFormat;

namespace Arcade.App;

/// <summary>
/// Draws the core's framebuffer: uploads it to a texture in its native pixel format (no CPU
/// conversion), rotates it in the vertex shader, and scales it to fit the window with the game's
/// aspect ratio using sharp-bilinear filtering (crisp pixels without uneven nearest-neighbour rows).
/// </summary>
sealed unsafe class VideoRenderer : IDisposable
{
    const string VertexShader = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        uniform int uRotation; // quarter turns counter-clockwise
        out vec2 vUv;
        void main()
        {
            vec2 uv = vec2(aPos.x * 0.5 + 0.5, 0.5 - aPos.y * 0.5); // v = 0 is the top row
            if (uRotation == 1) uv = vec2(1.0 - uv.y, uv.x);
            else if (uRotation == 2) uv = vec2(1.0 - uv.x, 1.0 - uv.y);
            else if (uRotation == 3) uv = vec2(uv.y, 1.0 - uv.x);
            vUv = uv;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    const string FragmentShader = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uTex;
        uniform vec2 uTexSize;
        uniform vec2 uPrescale; // integer scale per axis, in source orientation
        out vec4 fragColor;
        void main()
        {
            // Sharp bilinear: nearest-neighbour inside each source pixel, a 1-output-pixel blend at its edges.
            vec2 texel = vUv * uTexSize;
            vec2 centerDist = fract(texel) - 0.5;
            vec2 region = 0.5 - 0.5 / uPrescale;
            vec2 f = (centerDist - clamp(centerDist, -region, region)) * uPrescale + 0.5;
            fragColor = vec4(texture(uTex, (floor(texel) + f) / uTexSize).rgb, 1.0);
        }
        """;

    readonly GL _gl;
    readonly uint _program, _vao, _vbo, _texture;
    readonly int _uRotation, _uTexSize, _uPrescale;
    int _texWidth, _texHeight;
    RetroPixelFormat _texFormat;
    long _uploadedFrame = -1;

    public VideoRenderer(GL gl)
    {
        _gl = gl;
        _program = CreateProgram();
        _uRotation = _gl.GetUniformLocation(_program, "uRotation");
        _uTexSize = _gl.GetUniformLocation(_program, "uTexSize");
        _uPrescale = _gl.GetUniformLocation(_program, "uPrescale");

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        ReadOnlySpan<float> quad = [-1, -1, 1, -1, -1, 1, 1, 1];
        _gl.BufferData(BufferTargetARB.ArrayBuffer, quad, BufferUsageARB.StaticDraw);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 0, null);
        _gl.EnableVertexAttribArray(0);

        _texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    /// <param name="aspect">Display aspect ratio of the final (rotated) image; 0 means use square pixels.</param>
    public void Render(VideoFrame frame, uint rotation, float aspect, int windowWidth, int windowHeight)
    {
        _gl.Viewport(0, 0, (uint)windowWidth, (uint)windowHeight);
        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        if (frame.Width == 0 || windowWidth == 0 || windowHeight == 0)
            return;

        Upload(frame);

        var rotated = rotation % 2 == 1;
        if (aspect <= 0)
            aspect = rotated ? (float)frame.Height / frame.Width : (float)frame.Width / frame.Height;

        // Largest rectangle with the game's aspect ratio, centred in the window.
        int w = windowWidth, h = (int)Math.Round(windowWidth / aspect);
        if (h > windowHeight)
        {
            h = windowHeight;
            w = (int)Math.Round(windowHeight * aspect);
        }
        _gl.Viewport((windowWidth - w) / 2, (windowHeight - h) / 2, (uint)w, (uint)h);

        // The integer prescale is computed in source orientation, so swap axes for rotated games.
        var (outW, outH) = rotated ? (h, w) : (w, h);
        _gl.UseProgram(_program);
        _gl.Uniform1(_uRotation, (int)(rotation % 4));
        _gl.Uniform2(_uTexSize, (float)frame.Width, frame.Height);
        _gl.Uniform2(_uPrescale, MathF.Max(MathF.Floor((float)outW / frame.Width), 1), MathF.Max(MathF.Floor((float)outH / frame.Height), 1));
        _gl.BindVertexArray(_vao);
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
    }

    void Upload(VideoFrame frame)
    {
        if (frame.FrameNumber == _uploadedFrame && frame.Width == _texWidth && frame.Height == _texHeight)
            return;
        _uploadedFrame = frame.FrameNumber;

        var (internalFormat, format, type) = frame.Format switch
        {
            RetroPixelFormat.Xrgb8888 => (InternalFormat.Rgb8, GlPixelFormat.Bgra, PixelType.UnsignedByte),
            RetroPixelFormat.Rgb565 => (InternalFormat.Rgb8, GlPixelFormat.Rgb, PixelType.UnsignedShort565),
            _ => (InternalFormat.Rgb8, GlPixelFormat.Bgra, PixelType.UnsignedShort1555Rev), // 0RGB1555; top bit ignored
        };

        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, frame.Pitch / frame.BytesPerPixel);
        fixed (byte* pixels = frame.Data)
        {
            if (frame.Width != _texWidth || frame.Height != _texHeight || frame.Format != _texFormat)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, (uint)frame.Width, (uint)frame.Height, 0, format, type, pixels);
                (_texWidth, _texHeight, _texFormat) = (frame.Width, frame.Height, frame.Format);
            }
            else
            {
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)frame.Width, (uint)frame.Height, format, type, pixels);
            }
        }
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
    }

    uint CreateProgram()
    {
        var vs = Compile(ShaderType.VertexShader, VertexShader);
        var fs = Compile(ShaderType.FragmentShader, FragmentShader);
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
        _gl.DeleteTexture(_texture);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
    }
}
