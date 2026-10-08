namespace Arcade.Libretro;

/// <summary>
/// The most recent frame a core produced, copied out of core memory (the core's buffer is only
/// valid during the video callback). The buffer is reused between frames to avoid GC churn.
/// </summary>
public sealed class VideoFrame
{
    byte[] _buffer = Array.Empty<byte>();

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Pitch { get; private set; }
    public PixelFormat Format { get; private set; }
    public long FrameNumber { get; private set; }

    public ReadOnlySpan<byte> Data => _buffer.AsSpan(0, Pitch * Height);

    internal unsafe void CopyFrom(void* src, int width, int height, int pitch, PixelFormat format, long frameNumber)
    {
        var size = pitch * height;
        if (_buffer.Length < size)
            _buffer = new byte[size];
        new ReadOnlySpan<byte>(src, size).CopyTo(_buffer);
        Width = width;
        Height = height;
        Pitch = pitch;
        Format = format;
        FrameNumber = frameNumber;
    }

    public int BytesPerPixel => Format == PixelFormat.Xrgb8888 ? 4 : 2;

    /// <summary>FNV-1a 64 over the visible pixels only (pitch padding is ignored).</summary>
    public ulong ComputeHash()
    {
        ulong hash = 14695981039346656037UL;
        var rowBytes = Width * BytesPerPixel;
        for (var y = 0; y < Height; y++)
        {
            foreach (var b in _buffer.AsSpan(y * Pitch, rowBytes))
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
        }
        return hash;
    }

    /// <summary>Converts to tightly packed RGBA32. Allocates; not for the per-frame hot path.</summary>
    public Rgba32Image ToRgba32()
    {
        var dst = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            var row = _buffer.AsSpan(y * Pitch);
            for (var x = 0; x < Width; x++)
            {
                byte r, g, b;
                switch (Format)
                {
                    case PixelFormat.Xrgb8888:
                        b = row[x * 4];
                        g = row[x * 4 + 1];
                        r = row[x * 4 + 2];
                        break;
                    case PixelFormat.Rgb565:
                    {
                        var p = (ushort)(row[x * 2] | row[x * 2 + 1] << 8);
                        r = Expand5((p >> 11) & 0x1F);
                        g = Expand6((p >> 5) & 0x3F);
                        b = Expand5(p & 0x1F);
                        break;
                    }
                    default:
                    {
                        var p = (ushort)(row[x * 2] | row[x * 2 + 1] << 8);
                        r = Expand5((p >> 10) & 0x1F);
                        g = Expand5((p >> 5) & 0x1F);
                        b = Expand5(p & 0x1F);
                        break;
                    }
                }
                var o = (y * Width + x) * 4;
                dst[o] = r;
                dst[o + 1] = g;
                dst[o + 2] = b;
                dst[o + 3] = 255;
            }
        }
        return new Rgba32Image(Width, Height, dst);
    }

    static byte Expand5(int v) => (byte)(v << 3 | v >> 2);
    static byte Expand6(int v) => (byte)(v << 2 | v >> 4);
}

public sealed record Rgba32Image(int Width, int Height, byte[] Pixels)
{
    /// <summary>Rotates counter-clockwise by <paramref name="quarterTurns"/> × 90°, matching libretro's SET_ROTATION.</summary>
    public Rgba32Image RotateCcw(uint quarterTurns)
    {
        var img = this;
        for (var i = 0; i < quarterTurns % 4; i++)
            img = img.RotateCcw90();
        return img;
    }

    Rgba32Image RotateCcw90()
    {
        var dst = new byte[Pixels.Length];
        int nw = Height, nh = Width;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            // (x, y) -> (y, W-1-x)
            var s = (y * Width + x) * 4;
            var d = ((Width - 1 - x) * nw + y) * 4;
            Pixels.AsSpan(s, 4).CopyTo(dst.AsSpan(d, 4));
        }
        return new Rgba32Image(nw, nh, dst);
    }

    /// <summary>True if every pixel has the same colour (i.e. a blank/black screen).</summary>
    public bool IsUniform()
    {
        var first = Pixels.AsSpan(0, 4);
        for (var i = 4; i < Pixels.Length; i += 4)
            if (!Pixels.AsSpan(i, 4).SequenceEqual(first))
                return false;
        return true;
    }
}
