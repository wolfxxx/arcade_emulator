using Silk.NET.OpenGL;

namespace Arcade.App.Ui;

/// <summary>An RGBA8 OpenGL texture holding premultiplied-alpha pixels.</summary>
sealed unsafe class Texture : IDisposable
{
    readonly GL _gl;

    public uint Handle { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public float Aspect => Height == 0 ? 1 : (float)Width / Height;

    public Texture(GL gl, int width, int height, ReadOnlySpan<byte> rgba = default, bool mipmaps = false)
    {
        _gl = gl;
        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba.IsEmpty ? null : p);
        if (mipmaps)
            gl.GenerateMipmap(TextureTarget.Texture2D);
        Width = width;
        Height = height;
    }

    public void SetData(int x, int y, int width, int height, ReadOnlySpan<byte> rgba)
    {
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
    }

    /// <summary>Converts straight alpha to premultiplied alpha in place (decoded images are straight).</summary>
    public static void Premultiply(Span<byte> rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var a = rgba[i + 3];
            if (a == 255) continue;
            rgba[i] = (byte)(rgba[i] * a / 255);
            rgba[i + 1] = (byte)(rgba[i + 1] * a / 255);
            rgba[i + 2] = (byte)(rgba[i + 2] * a / 255);
        }
    }

    public void Dispose() => _gl.DeleteTexture(Handle);
}
