using Arcade.App.Ui;

namespace Arcade.App.Video;

enum PictureScaling
{
    /// <summary>As large as fits, keeping the shape.</summary>
    Fit,
    /// <summary>The largest whole multiple of the game's lines (every line the same height), keeping the shape.</summary>
    Integer,
    /// <summary>Fills the space, whatever the shape.</summary>
    Stretch,
}

enum PictureShape
{
    /// <summary>The shape of the monitor the game was made for (usually 4:3), so pixels may be a little wide or tall.</summary>
    Original,
    /// <summary>Every pixel square.</summary>
    SquarePixels,
}

enum PictureBackground
{
    Black,
    /// <summary>A dim, blurred glow of the game's colours around the picture.</summary>
    Glow,
}

/// <summary>How the game picture looks: for all games, or for one game in place of that.</summary>
sealed class PictureSettings
{
    /// <summary>A <see cref="ShaderLibrary"/> style id.</summary>
    public string Style { get; set; } = ShaderLibrary.DefaultId;
    public PictureScaling Scaling { get; set; } = PictureScaling.Fit;
    public PictureShape Shape { get; set; } = PictureShape.Original;
    public PictureBackground Background { get; set; } = PictureBackground.Black;
    /// <summary>Show bezel artwork around the game when there's some for it.</summary>
    public bool Bezel { get; set; } = true;
    /// <summary>Shader settings changed from their defaults, per style id.</summary>
    public Dictionary<string, Dictionary<string, float>> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PictureSettings Clone() => new()
    {
        Style = Style,
        Scaling = Scaling,
        Shape = Shape,
        Background = Background,
        Bezel = Bezel,
        Parameters = Parameters.ToDictionary(p => p.Key, p => new Dictionary<string, float>(p.Value), StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>The changed settings of the current style (created when missing).</summary>
    public Dictionary<string, float> StyleParameters()
    {
        if (!Parameters.TryGetValue(Style, out var values))
            Parameters[Style] = values = new Dictionary<string, float>();
        return values;
    }
}

/// <summary>Where the game picture goes on screen.</summary>
static class PictureLayout
{
    /// <summary>
    /// The game's rectangle inside <paramref name="area"/>, in whole pixels and centred.
    /// </summary>
    /// <param name="sourceW">The core's picture width, before any turning.</param>
    /// <param name="sourceH">The core's picture height (its number of lines).</param>
    /// <param name="turned">The picture is shown a quarter turn round, so the game's lines run up and down the screen.</param>
    /// <param name="originalAspect">The shape of the picture as shown on its original monitor, after turning (width / height).</param>
    public static RectF Place(RectF area, int sourceW, int sourceH, bool turned, double originalAspect, PictureScaling scaling, PictureShape shape)
    {
        if (sourceW <= 0 || sourceH <= 0 || area.W < 1 || area.H < 1)
            return area;
        var squareAspect = turned ? (double)sourceH / sourceW : (double)sourceW / sourceH;
        var aspect = shape == PictureShape.SquarePixels || originalAspect <= 0 ? squareAspect : originalAspect;

        RectF rect;
        switch (scaling)
        {
            case PictureScaling.Stretch:
                rect = area;
                break;
            case PictureScaling.Integer:
                rect = IntegerFit(area, sourceW, sourceH, turned, aspect, shape == PictureShape.SquarePixels) ?? area.Fit((float)aspect);
                break;
            default:
                rect = area.Fit((float)aspect);
                break;
        }
        var w = MathF.Round(rect.W);
        var h = MathF.Round(rect.H);
        return new RectF(MathF.Round(area.X + (area.W - w) / 2), MathF.Round(area.Y + (area.H - h) / 2), w, h);
    }

    /// <summary>The largest size with a whole number of screen pixels per game line, or null if not even one fits.</summary>
    static RectF? IntegerFit(RectF area, int sourceW, int sourceH, bool turned, double aspect, bool squarePixels)
    {
        // Lines run across the screen (or up and down it when turned); their count is sourceH.
        var along = turned ? area.H : area.W;  // length available along a line
        var across = turned ? area.W : area.H; // length available across the lines
        var lineAspect = turned ? 1 / aspect : aspect; // picture length along the lines / across them
        for (var k = (int)(across / sourceH); k >= 1; k--)
        {
            var acrossLen = (float)(k * sourceH);
            var alongLen = squarePixels ? k * sourceW : (float)Math.Round(acrossLen * lineAspect);
            if (alongLen <= along + 0.5f)
                return turned ? new RectF(0, 0, acrossLen, alongLen) : new RectF(0, 0, alongLen, acrossLen);
        }
        return null;
    }
}
