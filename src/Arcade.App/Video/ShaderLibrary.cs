using System.Reflection;

namespace Arcade.App.Video;

/// <summary>A look the game picture can have: a built-in style or a preset from the shaders folder.</summary>
sealed record PictureStyle(string Id, string Name, string Description, bool BuiltIn);

/// <summary>
/// The picture styles on offer. The built-in ones are GLSL presets stored inside the app; any
/// RetroArch GLSL preset (<c>.glslp</c>) under the shaders folder is listed too, by its path there.
/// </summary>
sealed class ShaderLibrary(string userFolder)
{
    const string BuiltInPrefix = "builtin:/";
    public const string DefaultId = "sharp";

    public static readonly IReadOnlyList<PictureStyle> BuiltIn =
    [
        new("sharp", "Sharp pixels", "Crisp, evenly sized pixels, as the game drew them", true),
        new("scanlines", "Scanlines", "Each line of the game drawn as a soft beam with dark gaps between", true),
        new("crt", "CRT", "An arcade monitor: beams, glow and the tube's coloured phosphors", true),
        new("crt-curved", "CRT, curved", "The CRT look on a curved screen with rounded corners", true),
    ];

    List<PictureStyle>? _user;

    public string UserFolder => userFolder;

    /// <summary>Built-in styles first, then the shaders folder's presets by path.</summary>
    public IReadOnlyList<PictureStyle> All() => [.. BuiltIn, .. UserStyles()];

    /// <summary>Looks at the shaders folder again (e.g. after files were added).</summary>
    public void Refresh() => _user = null;

    public PictureStyle? Find(string? id) =>
        id == null ? null : BuiltIn.FirstOrDefault(s => s.Id == id) ?? UserStyles().FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    IReadOnlyList<PictureStyle> UserStyles()
    {
        if (_user != null)
            return _user;
        _user = new List<PictureStyle>();
        if (!Directory.Exists(userFolder))
            return _user;
        try
        {
            foreach (var file in Directory.EnumerateFiles(userFolder, "*.glslp", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                var id = Path.GetRelativePath(userFolder, file).Replace('\\', '/');
                var folder = Path.GetDirectoryName(id)?.Replace('\\', '/');
                var name = Path.GetFileNameWithoutExtension(file);
                _user.Add(new PictureStyle(id, string.IsNullOrEmpty(folder) ? name : $"{name} ({folder})", $"shaders/{id}", false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not list shaders: {e.Message}");
        }
        return _user;
    }

    /// <summary>Reads a style's preset.</summary>
    /// <exception cref="ShaderException">It's missing or can't be read.</exception>
    public ShaderPreset Load(string id)
    {
        var builtIn = BuiltIn.Any(s => s.Id == id);
        var path = builtIn ? BuiltInPrefix + id + ".glslp" : Path.Combine(userFolder, id).Replace('\\', '/');
        try
        {
            return ShaderPreset.Parse(path, ReadText);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ShaderException($"Can't read {Path.GetFileName(path)}: {e.Message}");
        }
    }

    public string ReadText(string path)
    {
        if (!path.StartsWith(BuiltInPrefix, StringComparison.Ordinal))
            return File.ReadAllText(path);
        using var reader = new StreamReader(OpenBuiltIn(path));
        return reader.ReadToEnd();
    }

    public byte[] ReadBytes(string path)
    {
        if (!path.StartsWith(BuiltInPrefix, StringComparison.Ordinal))
            return File.ReadAllBytes(path);
        using var stream = OpenBuiltIn(path);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    static Stream OpenBuiltIn(string path) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("shaders/" + path[BuiltInPrefix.Length..])
        ?? throw new FileNotFoundException($"No built-in shader file {path[BuiltInPrefix.Length..]}");
}
