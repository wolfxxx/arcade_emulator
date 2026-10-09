using Silk.NET.OpenGL;

namespace Arcade.App;

/// <summary>
/// Turns the whole picture for a monitor mounted on its side (a "TATE" cabinet). Each frame is
/// drawn upright into an offscreen texture at the rotated size, then copied to the window turned
/// by a quarter, half or three-quarter turn.
/// </summary>
sealed unsafe class ScreenRotator : IDisposable
{
    const string VertexShader = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        uniform int uTurns; // quarter turns clockwise
        out vec2 vUv;
        void main()
        {
            // Screen position, top-left origin, 0..1.
            vec2 s = vec2(aPos.x * 0.5 + 0.5, 0.5 - aPos.y * 0.5);
            // Which point of the upright picture shows here when it's turned clockwise.
            vec2 p = s;
            if (uTurns == 1) p = vec2(s.y, 1.0 - s.x);
            else if (uTurns == 2) p = vec2(1.0 - s.x, 1.0 - s.y);
            else if (uTurns == 3) p = vec2(1.0 - s.y, s.x);
            vUv = vec2(p.x, 1.0 - p.y); // the texture's rows run bottom-up
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    const string FragmentShader = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uTex;
        out vec4 fragColor;
        void main() { fragColor = vec4(texture(uTex, vUv).rgb, 1.0); }
        """;

    readonly GL _gl;
    readonly uint _program, _vao, _vbo, _fbo, _texture;
    readonly int _uTurns;
    int _width, _height;

    public ScreenRotator(GL gl)
    {
        _gl = gl;
        _program = CreateProgram();
        _uTurns = _gl.GetUniformLocation(_program, "uTurns");
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
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _fbo = _gl.GenFramebuffer();
    }

    /// <summary>The upright picture's size for a window of the given size.</summary>
    public static (int W, int H) LogicalSize(int windowW, int windowH, int turns) =>
        turns % 2 == 1 ? (windowH, windowW) : (windowW, windowH);

    /// <summary>Starts drawing the upright picture at this size.</summary>
    public void Begin(int width, int height)
    {
        if (width != _width || height != _height)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _texture, 0);
            (_width, _height) = (width, height);
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
    }

    /// <summary>Draws the picture into the currently bound framebuffer (the window), turned.</summary>
    public void Present(int windowW, int windowH, int turns)
    {
        _gl.Viewport(0, 0, (uint)windowW, (uint)windowH);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.UseProgram(_program);
        _gl.Uniform1(_uTurns, turns % 4);
        _gl.BindVertexArray(_vao);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
    }

    /// <summary>Maps a window position to the upright picture (for mouse clicks).</summary>
    public static (float X, float Y) ToLogical(float x, float y, int windowW, int windowH, int turns) => (turns % 4) switch
    {
        // Inverse of the shader: window point s shows picture point p.
        1 => (y, windowW - x),
        2 => (windowW - x, windowH - y),
        3 => (windowH - y, x),
        _ => (x, y),
    };

    uint CreateProgram()
    {
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

    public void Dispose()
    {
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteTexture(_texture);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
    }
}
