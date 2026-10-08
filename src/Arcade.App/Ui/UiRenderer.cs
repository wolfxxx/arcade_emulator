using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using FontStashSharp;
using FontStashSharp.Interfaces;
using Silk.NET.OpenGL;

namespace Arcade.App.Ui;

/// <summary>
/// Draws the interface in screen pixels with one batched shader: rounded rectangles and outlines
/// (anti-aliased with a signed distance function), images, gradients, and FontStashSharp text.
/// Everything uses premultiplied alpha. Draw calls are batched until the texture or clip changes.
/// </summary>
sealed unsafe class UiRenderer : IFontStashRenderer2, ITexture2DManager, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex
    {
        public Vector2 Position;
        public Vector2 Uv;
        public uint Color;
        public Vector2 Local;  // position relative to the shape's centre, in pixels
        public Vector4 Shape;  // half width, half height, corner radius, mode (0 plain, 1 rounded fill, 2+t outline of width t)
    }

    const string VertexShader = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec4 aColor;
        layout(location = 3) in vec2 aLocal;
        layout(location = 4) in vec4 aShape;
        uniform vec2 uScreen;
        out vec2 vUv; out vec4 vColor; out vec2 vLocal; out vec4 vShape;
        void main()
        {
            vUv = aUv; vColor = aColor; vLocal = aLocal; vShape = aShape;
            gl_Position = vec4(aPos.x / uScreen.x * 2.0 - 1.0, 1.0 - aPos.y / uScreen.y * 2.0, 0.0, 1.0);
        }
        """;

    const string FragmentShader = """
        #version 330 core
        in vec2 vUv; in vec4 vColor; in vec2 vLocal; in vec4 vShape;
        uniform sampler2D uTex;
        out vec4 fragColor;
        void main()
        {
            float coverage = 1.0;
            if (vShape.w > 0.5)
            {
                vec2 q = abs(vLocal) - vShape.xy + vShape.z;
                float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - vShape.z;
                coverage = clamp(0.5 - d, 0.0, 1.0);
                if (vShape.w > 1.5)
                    coverage *= clamp(0.5 + d + (vShape.w - 2.0), 0.0, 1.0);
            }
            fragColor = texture(uTex, vUv) * vColor * coverage;
        }
        """;

    readonly GL _gl;
    readonly uint _program, _vao, _vbo, _ebo;
    readonly int _uScreen;
    readonly Texture _white;
    Vertex[] _vertices = new Vertex[4096];
    int _quadCount;
    uint _currentTexture;
    readonly Stack<Rectangle> _clips = new();

    public GL Gl => _gl;
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Layout scale: 1.0 at 1920×1080, so designs can use fixed 1080p numbers.</summary>
    public float Scale { get; private set; } = 1;

    public UiRenderer(GL gl)
    {
        _gl = gl;
        _program = CreateProgram();
        _uScreen = _gl.GetUniformLocation(_program, "uScreen");
        _white = new Texture(gl, 1, 1, [255, 255, 255, 255]);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);

        // Quads share one index buffer: 0-1-2, 2-1-3 per quad.
        var indices = new ushort[_vertices.Length / 4 * 6];
        for (int q = 0, i = 0; i < indices.Length; q++, i += 6)
        {
            var v = (ushort)(q * 4);
            (indices[i], indices[i + 1], indices[i + 2], indices[i + 3], indices[i + 4], indices[i + 5]) =
                (v, (ushort)(v + 1), (ushort)(v + 2), (ushort)(v + 2), (ushort)(v + 1), (ushort)(v + 3));
        }
        _gl.BufferData<ushort>(BufferTargetARB.ElementArrayBuffer, indices, BufferUsageARB.StaticDraw);

        var stride = (uint)sizeof(Vertex);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)8);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)16);
        _gl.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, stride, (void*)20);
        _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)28);
        for (uint i = 0; i < 5; i++)
            _gl.EnableVertexAttribArray(i);
    }

    public ITexture2DManager TextureManager => this;

    public float S(float designPixels) => designPixels * Scale;

    public void Begin(int width, int height)
    {
        Width = width;
        Height = height;
        Scale = Math.Min(width / 1920f, height / 1080f);
        _quadCount = 0;
        _currentTexture = 0;
        _clips.Clear();
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
    }

    public void End()
    {
        Flush();
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ScissorTest);
    }

    // ---- Clipping ----

    public void PushClip(RectF rect)
    {
        var r = Rectangle.FromLTRB((int)MathF.Floor(rect.X), (int)MathF.Floor(rect.Y), (int)MathF.Ceiling(rect.Right), (int)MathF.Ceiling(rect.Bottom));
        if (_clips.Count > 0)
            r = Rectangle.Intersect(r, _clips.Peek());
        Flush();
        _clips.Push(r);
        ApplyClip();
    }

    public void PopClip()
    {
        Flush();
        _clips.Pop();
        ApplyClip();
    }

    void ApplyClip()
    {
        if (_clips.Count == 0)
        {
            _gl.Disable(EnableCap.ScissorTest);
            return;
        }
        var r = _clips.Peek();
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(r.X, Height - r.Bottom, (uint)Math.Max(r.Width, 0), (uint)Math.Max(r.Height, 0)); // GL's origin is bottom-left
    }

    // ---- Shapes ----

    public void Fill(RectF r, Rgba color, float radius = 0) => Quad(_white.Handle, r, color, color, radius, radius > 0 ? 1 : 0);

    /// <summary>Vertical gradient from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
    public void FillGradient(RectF r, Rgba top, Rgba bottom, float radius = 0) => Quad(_white.Handle, r, top, bottom, radius, radius > 0 ? 1 : 0);

    public void Outline(RectF r, Rgba color, float thickness, float radius = 0) =>
        Quad(_white.Handle, r, color, color, Math.Max(radius, 0.001f), 2 + thickness);

    public void Image(Texture texture, RectF r, float alpha = 1, float radius = 0) =>
        Quad(texture.Handle, r, Rgba.White.WithAlpha(alpha), Rgba.White.WithAlpha(alpha), radius, radius > 0 ? 1 : 0);

    /// <summary>Soft drop shadow: a few expanding, fading rounded rectangles.</summary>
    public void Shadow(RectF r, float radius, float spread, float alpha = 0.35f)
    {
        const int steps = 6;
        for (var i = 1; i <= steps; i++)
        {
            var grow = spread * i / steps;
            Fill(new RectF(r.X - grow, r.Y - grow + spread * 0.3f, r.W + 2 * grow, r.H + 2 * grow), Rgba.Black.WithAlpha(alpha / steps), radius + grow);
        }
    }

    void Quad(uint texture, RectF r, Rgba top, Rgba bottom, float radius, float mode)
    {
        if (r.W <= 0 || r.H <= 0)
            return;
        radius = Math.Min(radius, Math.Min(r.W, r.H) / 2);
        var shape = new Vector4(r.W / 2, r.H / 2, radius, mode);
        var (ct, cb) = (top.Premultiplied, bottom.Premultiplied);
        var (hw, hh) = (r.W / 2, r.H / 2);
        var v = Reserve(texture);
        v[0] = new Vertex { Position = new(r.X, r.Y), Uv = new(0, 0), Color = ct, Local = new(-hw, -hh), Shape = shape };
        v[1] = new Vertex { Position = new(r.Right, r.Y), Uv = new(1, 0), Color = ct, Local = new(hw, -hh), Shape = shape };
        v[2] = new Vertex { Position = new(r.X, r.Bottom), Uv = new(0, 1), Color = cb, Local = new(-hw, hh), Shape = shape };
        v[3] = new Vertex { Position = new(r.Right, r.Bottom), Uv = new(1, 1), Color = cb, Local = new(hw, hh), Shape = shape };
    }

    Span<Vertex> Reserve(uint texture)
    {
        if (texture != _currentTexture || (_quadCount + 1) * 4 > _vertices.Length)
        {
            Flush();
            _currentTexture = texture;
        }
        var span = _vertices.AsSpan(_quadCount * 4, 4);
        _quadCount++;
        return span;
    }

    void Flush()
    {
        if (_quadCount == 0)
            return;
        _gl.UseProgram(_program);
        _gl.Uniform2(_uScreen, (float)Width, Height);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (Vertex* p = _vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_quadCount * 4 * sizeof(Vertex)), p, BufferUsageARB.StreamDraw);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _currentTexture);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)(_quadCount * 6), DrawElementsType.UnsignedShort, null);
        _quadCount = 0;
    }

    // ---- Text ----

    public void Text(SpriteFontBase font, string text, float x, float y, Rgba color) =>
        font.DrawText(this, text, new Vector2(x, y), ToFs(color));

    /// <summary>Draws text cut with an ellipsis to fit <paramref name="maxWidth"/>.</summary>
    public void Text(SpriteFontBase font, string text, float x, float y, Rgba color, float maxWidth) =>
        Text(font, Ellipsize(font, text, maxWidth), x, y, color);

    public void TextCentered(SpriteFontBase font, string text, float centerX, float y, Rgba color) =>
        Text(font, text, centerX - Measure(font, text).X / 2, y, color);

    public void TextRight(SpriteFontBase font, string text, float right, float y, Rgba color) =>
        Text(font, text, right - Measure(font, text).X, y, color);

    public static Vector2 Measure(SpriteFontBase font, string text) => text.Length == 0 ? Vector2.Zero : font.MeasureString(text);

    public static string Ellipsize(SpriteFontBase font, string text, float maxWidth)
    {
        if (Measure(font, text).X <= maxWidth)
            return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Measure(font, text[..mid].TrimEnd() + "…").X <= maxWidth) lo = mid; else hi = mid - 1;
        }
        return text[..lo].TrimEnd() + "…";
    }

    /// <summary>Splits text into lines no wider than <paramref name="maxWidth"/>, breaking at spaces.</summary>
    public static List<string> Wrap(SpriteFontBase font, string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(font, candidate).X > maxWidth)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    static FSColor ToFs(Rgba c)
    {
        var a = c.A / 255f;
        return new FSColor((byte)(c.R * a), (byte)(c.G * a), (byte)(c.B * a), c.A);
    }

    void IFontStashRenderer2.DrawQuad(object texture, ref VertexPositionColorTexture topLeft, ref VertexPositionColorTexture topRight,
        ref VertexPositionColorTexture bottomLeft, ref VertexPositionColorTexture bottomRight)
    {
        var v = Reserve(((Texture)texture).Handle);
        v[0] = Glyph(topLeft);
        v[1] = Glyph(topRight);
        v[2] = Glyph(bottomLeft);
        v[3] = Glyph(bottomRight);
    }

    static Vertex Glyph(in VertexPositionColorTexture g) => new()
    {
        Position = new(g.Position.X, g.Position.Y),
        Uv = g.TextureCoordinate,
        Color = g.Color.PackedValue, // FSColor packs R in the lowest byte, like our vertices
    };

    object ITexture2DManager.CreateTexture(int width, int height) => new Texture(_gl, width, height);

    Point ITexture2DManager.GetTextureSize(object texture) => new(((Texture)texture).Width, ((Texture)texture).Height);

    void ITexture2DManager.SetTextureData(object texture, Rectangle bounds, byte[] data)
    {
        Flush(); // glyphs already batched must be drawn before the atlas changes under them
        ((Texture)texture).SetData(bounds.X, bounds.Y, bounds.Width, bounds.Height, data);
    }

    // ---- Setup ----

    uint CreateProgram()
    {
        uint Compile(ShaderType type, string source)
        {
            var shader = _gl.CreateShader(type);
            _gl.ShaderSource(shader, source);
            _gl.CompileShader(shader);
            _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var ok);
            if (ok == 0)
                throw new InvalidOperationException($"UI {type} compile failed: {_gl.GetShaderInfoLog(shader)}");
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
            throw new InvalidOperationException("UI shader link failed: " + _gl.GetProgramInfoLog(program));
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        _gl.UseProgram(program);
        _gl.Uniform1(_gl.GetUniformLocation(program, "uTex"), 0);
        return program;
    }

    public void Dispose()
    {
        _white.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
    }
}
