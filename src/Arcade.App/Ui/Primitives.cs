using System.Globalization;

namespace Arcade.App.Ui;

/// <summary>A straight-alpha colour. The renderer premultiplies it when drawing.</summary>
readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Rgba White = new(255, 255, 255);
    public static readonly Rgba Black = new(0, 0, 0);
    public static readonly Rgba Transparent = new(0, 0, 0, 0);

    public Rgba WithAlpha(float alpha) => this with { A = (byte)Math.Clamp(A * alpha, 0, 255) };

    public static Rgba Lerp(Rgba a, Rgba b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t), (byte)(a.A + (b.A - a.A) * t));

    /// <summary>Packed premultiplied RGBA for the vertex buffer (R in the lowest byte).</summary>
    public uint Premultiplied
    {
        get
        {
            var a = A / 255f;
            return (uint)(R * a) | (uint)(G * a) << 8 | (uint)(B * a) << 16 | (uint)A << 24;
        }
    }

    /// <summary>Parses "#rgb", "#rrggbb" or "#rrggbbaa".</summary>
    public static Rgba Parse(string text)
    {
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"'{text}' is not a colour; use #rrggbb or #rrggbbaa.");
        if (hex.Length == 6)
            v = v << 8 | 0xFF;
        return new((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public override string ToString() => $"#{R:x2}{G:x2}{B:x2}{A:x2}";
}

readonly record struct RectF(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public float CenterX => X + W / 2;
    public float CenterY => Y + H / 2;

    public RectF Inset(float d) => new(X + d, Y + d, W - 2 * d, H - 2 * d);
    public RectF Inset(float dx, float dy) => new(X + dx, Y + dy, W - 2 * dx, H - 2 * dy);
    public bool Contains(float x, float y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>The largest rectangle with the given aspect ratio, centred inside this one.</summary>
    public RectF Fit(float aspect)
    {
        if (aspect <= 0) return this;
        var w = W;
        var h = W / aspect;
        if (h > H)
        {
            h = H;
            w = H * aspect;
        }
        return new(X + (W - w) / 2, Y + (H - h) / 2, w, h);
    }
}

static class Easing
{
    /// <summary>Frame-rate independent exponential approach: moves <paramref name="current"/> toward <paramref name="target"/>.</summary>
    public static float Approach(float current, float target, float speed, float dt)
    {
        var t = 1 - MathF.Exp(-speed * dt);
        var next = current + (target - current) * t;
        return MathF.Abs(next - target) < 0.01f ? target : next;
    }

    public static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
