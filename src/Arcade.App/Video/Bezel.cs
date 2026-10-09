using System.Text.Json;
using Arcade.App.Ui;

namespace Arcade.App.Video;

/// <summary>
/// Artwork drawn around the game, like a cabinet's bezel: a picture with a see-through window that
/// the game shows through. The window is found from the picture's transparency, or read from a
/// <c>.json</c> file of the same name (<c>{"x":…,"y":…,"width":…,"height":…}</c> in image pixels).
/// </summary>
sealed class Bezel
{
    public required string Path { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>The see-through window, in image pixels.</summary>
    public RectF Window { get; init; }
    /// <summary>Premultiplied RGBA, for uploading.</summary>
    public required byte[] Pixels { get; init; }

    /// <summary>Where the bezel and the game's window go when the bezel is fitted inside <paramref name="area"/>.</summary>
    public (RectF Image, RectF Window) Place(RectF area)
    {
        var image = area.Fit((float)Width / Height);
        var scale = image.W / Width;
        return (image, new RectF(image.X + Window.X * scale, image.Y + Window.Y * scale, Window.W * scale, Window.H * scale));
    }

    /// <summary>
    /// The bezel for a game: <c>bezels/&lt;set&gt;.png</c>, then its parent's, then
    /// <c>bezels/default-vertical.png</c> or <c>default-horizontal.png</c>, then <c>bezels/default.png</c>.
    /// </summary>
    public static string? Find(string artworkRoot, string setName, string? parent, bool vertical)
    {
        foreach (var folder in new[] { "bezels", "bezel" })
        {
            var dir = System.IO.Path.Combine(artworkRoot, folder);
            if (!Directory.Exists(dir))
                continue;
            string[] names = [setName, .. parent != null ? new[] { parent } : [], vertical ? "default-vertical" : "default-horizontal", "default"];
            foreach (var name in names)
            {
                var path = System.IO.Path.Combine(dir, name + ".png");
                if (File.Exists(path))
                    return path;
            }
        }
        return null;
    }

    /// <summary>Loads a bezel picture and finds its window.</summary>
    /// <exception cref="InvalidDataException">The picture can't be read or has no see-through window.</exception>
    public static Bezel Load(string path)
    {
        StbImageSharp.ImageResult image;
        try
        {
            image = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(path), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"Can't read the bezel {System.IO.Path.GetFileName(path)}: {e.Message}");
        }
        var window = ReadWindowFile(System.IO.Path.ChangeExtension(path, ".json"))
            ?? FindWindow(image.Data, image.Width, image.Height)
            ?? throw new InvalidDataException($"The bezel {System.IO.Path.GetFileName(path)} has no see-through window for the game");
        Texture.Premultiply(image.Data);
        return new Bezel { Path = path, Width = image.Width, Height = image.Height, Window = window, Pixels = image.Data };
    }

    static RectF? ReadWindowFile(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            float Get(string name) => root.TryGetProperty(name, out var v) ? v.GetSingle() : throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} needs \"{name}\"");
            return new RectF(Get("x"), Get("y"), Get("width"), Get("height"));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"Can't read {System.IO.Path.GetFileName(path)}: {e.Message}");
        }
    }

    /// <summary>
    /// The bounding box of the see-through area around the middle of the picture (alpha below half),
    /// or null if the middle isn't see-through or the area is tiny.
    /// </summary>
    public static RectF? FindWindow(byte[] rgba, int width, int height)
    {
        bool Clear(int x, int y) => rgba[(y * width + x) * 4 + 3] < 128;
        int cx = width / 2, cy = height / 2;
        if (!Clear(cx, cy))
            return null;

        // Flood fill from the middle, so see-through details elsewhere (e.g. lit buttons) don't count.
        var seen = new bool[width * height];
        var stack = new Stack<int>();
        stack.Push(cy * width + cx);
        seen[cy * width + cx] = true;
        int left = cx, right = cx, top = cy, bottom = cy;
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            int x = i % width, y = i / width;
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
            void Visit(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    return;
                var n = ny * width + nx;
                if (seen[n])
                    return;
                seen[n] = true;
                if (rgba[n * 4 + 3] < 128)
                    stack.Push(n);
            }
            Visit(x - 1, y);
            Visit(x + 1, y);
            Visit(x, y - 1);
            Visit(x, y + 1);
        }
        var w = right - left + 1;
        var h = bottom - top + 1;
        if (w < width / 8 || h < height / 8)
            return null;
        return new RectF(left, top, w, h);
    }
}
