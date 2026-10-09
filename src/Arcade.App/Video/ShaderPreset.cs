using System.Globalization;

namespace Arcade.App.Video;

/// <summary>How a pass's output size is worked out, per axis.</summary>
enum ScaleType
{
    /// <summary>A multiple of the pass's input size.</summary>
    Source,
    /// <summary>A multiple of the game's size on screen.</summary>
    Viewport,
    /// <summary>A fixed number of pixels.</summary>
    Absolute,
}

enum WrapMode { ClampToBorder, ClampToEdge, Repeat, MirroredRepeat }

/// <summary>One shader in a chain, with the settings the preset gave it.</summary>
sealed record ShaderPass
{
    public required string Path { get; init; }
    public ScaleType ScaleTypeX { get; init; } = ScaleType.Source;
    public ScaleType ScaleTypeY { get; init; } = ScaleType.Source;
    public float ScaleX { get; init; } = 1;
    public float ScaleY { get; init; } = 1;
    /// <summary>Whether this pass reads its input with bilinear filtering; null means the default (linear).</summary>
    public bool? FilterLinear { get; init; }
    public WrapMode Wrap { get; init; } = WrapMode.ClampToBorder;
    public bool FloatFramebuffer { get; init; }
    public bool SrgbFramebuffer { get; init; }
    public bool MipmapInput { get; init; }
    /// <summary>FrameCount is given modulo this when it's above 0.</summary>
    public int FrameCountMod { get; init; }
    /// <summary>Lets later passes read this pass's output as <c>&lt;Alias&gt;Texture</c>.</summary>
    public string? Alias { get; init; }

    /// <summary>True when the pass draws straight onto the screen at the game's size.</summary>
    public bool IsViewportSized => ScaleTypeX == ScaleType.Viewport && ScaleTypeY == ScaleType.Viewport && ScaleX == 1 && ScaleY == 1;
}

/// <summary>A picture (look-up table) the shaders read, such as a shadow-mask pattern.</summary>
sealed record PresetTexture(string Name, string Path, bool Linear, WrapMode Wrap, bool Mipmap);

/// <summary>
/// A shader chain in RetroArch's GLSL preset format (<c>.glslp</c>): a list of passes, each a
/// <c>.glsl</c> file with its scaling and filtering, plus look-up textures and parameter values.
/// The same format is used for the built-in picture styles and for presets in the shaders folder.
/// </summary>
sealed class ShaderPreset
{
    public List<ShaderPass> Passes { get; } = new();
    public List<PresetTexture> Textures { get; } = new();
    /// <summary>Parameter values the preset sets, replacing the shaders' own defaults.</summary>
    public Dictionary<string, float> Parameters { get; } = new(StringComparer.Ordinal);

    /// <summary>Reads a preset. <paramref name="readFile"/> loads any file by path (built-in presets live inside the app).</summary>
    /// <exception cref="ShaderException">The preset is malformed or uses something unsupported.</exception>
    public static ShaderPreset Parse(string path, Func<string, string> readFile) => Parse(path, readFile, 0);

    static ShaderPreset Parse(string path, Func<string, string> readFile, int depth)
    {
        if (depth > 8)
            throw new ShaderException($"{FileName(path)}: presets refer to each other in a loop");
        if (path.EndsWith(".slangp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slang", StringComparison.OrdinalIgnoreCase))
            throw new ShaderException($"{FileName(path)} is a Vulkan (slang) shader; use the GLSL version (.glslp)");

        var text = readFile(path);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ShaderPreset? preset = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#reference", StringComparison.OrdinalIgnoreCase))
            {
                // A preset that starts from another one and changes some of its values.
                var target = Combine(path, Unquote(line["#reference".Length..].Trim()));
                var referenced = Parse(target, readFile, depth + 1);
                if (preset == null)
                    preset = referenced;
                else
                    preset.Merge(referenced);
                continue;
            }
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var value = line[(eq + 1)..].Trim();
            // Values may be quoted, and some presets add trailing comments.
            if (!value.StartsWith('"') && value.IndexOf(" #", StringComparison.Ordinal) is var hash and > 0)
                value = value[..hash].Trim();
            values[line[..eq].Trim()] = Unquote(value);
        }

        preset ??= new ShaderPreset();
        if (values.TryGetValue("shaders", out var countText))
        {
            // Read the leading digits, as RetroArch does, so stray characters after the number don't matter.
            var digits = new string(countText.TakeWhile(char.IsAsciiDigit).ToArray());
            if (!int.TryParse(digits, out var count) || count < 1 || count > 64)
                throw new ShaderException($"{FileName(path)}: 'shaders = {countText}' isn't a pass count");
            preset.Passes.Clear();
            for (var i = 0; i < count; i++)
                preset.Passes.Add(ReadPass(path, values, i, last: i == count - 1));
        }
        else if (preset.Passes.Count == 0)
        {
            throw new ShaderException($"{FileName(path)} doesn't list any shaders");
        }

        if (values.TryGetValue("textures", out var textureList))
            foreach (var name in textureList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!values.TryGetValue(name, out var texturePath))
                    throw new ShaderException($"{FileName(path)}: texture '{name}' has no file");
                preset.Textures.RemoveAll(t => t.Name == name);
                preset.Textures.Add(new PresetTexture(name, Combine(path, texturePath),
                    Bool(values, name + "_linear") ?? true, Wrap(values, name + "_wrap_mode") ?? WrapMode.ClampToBorder, Bool(values, name + "_mipmap") ?? false));
            }

        // Anything else that's a number may be a parameter value (with or without a "parameters" list).
        var known = KnownKeys(values, preset);
        foreach (var (key, value) in values)
            if (!known.Contains(key) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                preset.Parameters[key] = number;
        return preset;
    }

    static ShaderPass ReadPass(string presetPath, Dictionary<string, string> values, int i, bool last)
    {
        if (!values.TryGetValue($"shader{i}", out var file))
            throw new ShaderException($"{FileName(presetPath)}: shader{i} is missing");
        if (file.EndsWith(".slang", StringComparison.OrdinalIgnoreCase))
            throw new ShaderException($"{FileName(presetPath)} uses Vulkan (slang) shaders; use the GLSL version");

        var type = Scale(values, $"scale_type{i}");
        var typeX = Scale(values, $"scale_type_x{i}") ?? type;
        var typeY = Scale(values, $"scale_type_y{i}") ?? type;
        var scale = Float(values, $"scale{i}");
        var scaleX = Float(values, $"scale_x{i}") ?? scale;
        var scaleY = Float(values, $"scale_y{i}") ?? scale;
        // Without a scale type, the last pass draws at the game's size on screen and the others at their input's size.
        var fallback = typeX == null && typeY == null && scaleX == null && scaleY == null && last ? ScaleType.Viewport : ScaleType.Source;

        return new ShaderPass
        {
            Path = Combine(presetPath, file),
            ScaleTypeX = typeX ?? fallback,
            ScaleTypeY = typeY ?? fallback,
            ScaleX = scaleX ?? 1,
            ScaleY = scaleY ?? 1,
            FilterLinear = Bool(values, $"filter_linear{i}"),
            Wrap = Wrap(values, $"wrap_mode{i}") ?? WrapMode.ClampToBorder,
            FloatFramebuffer = Bool(values, $"float_framebuffer{i}") ?? false,
            SrgbFramebuffer = Bool(values, $"srgb_framebuffer{i}") ?? false,
            MipmapInput = Bool(values, $"mipmap_input{i}") ?? false,
            FrameCountMod = values.TryGetValue($"frame_count_mod{i}", out var mod) && int.TryParse(mod, out var m) ? m : 0,
            Alias = values.TryGetValue($"alias{i}", out var alias) && alias.Length > 0 ? alias : null,
        };
    }

    void Merge(ShaderPreset other)
    {
        foreach (var texture in other.Textures)
        {
            Textures.RemoveAll(t => t.Name == texture.Name);
            Textures.Add(texture);
        }
        foreach (var (name, value) in other.Parameters)
            Parameters[name] = value;
    }

    static HashSet<string> KnownKeys(Dictionary<string, string> values, ShaderPreset preset)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shaders", "textures", "parameters", "feedback_pass" };
        string[] perPass = ["shader", "scale_type", "scale_type_x", "scale_type_y", "scale", "scale_x", "scale_y", "filter_linear",
            "wrap_mode", "float_framebuffer", "srgb_framebuffer", "mipmap_input", "frame_count_mod", "alias"];
        for (var i = 0; i < Math.Max(preset.Passes.Count, 64); i++)
            foreach (var key in perPass)
                known.Add(key + i);
        foreach (var texture in preset.Textures)
        {
            known.Add(texture.Name);
            known.Add(texture.Name + "_linear");
            known.Add(texture.Name + "_wrap_mode");
            known.Add(texture.Name + "_mipmap");
        }
        return known;
    }

    static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value.IndexOf('"', 1) is var end and > 0 ? value[1..end]
        : value.Trim('"'); // a quote left open

    static string FileName(string path) => System.IO.Path.GetFileName(path);

    /// <summary>A path written in a preset, relative to the preset's folder.</summary>
    public static string Combine(string presetPath, string relative)
    {
        relative = relative.Replace('\\', '/');
        var folder = presetPath.Replace('\\', '/');
        var slash = folder.LastIndexOf('/');
        folder = slash < 0 ? "" : folder[..slash];
        if (relative.StartsWith('/') || (relative.Length > 1 && relative[1] == ':'))
            return relative;
        var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
        foreach (var part in relative.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0 && parts[^1] is not ".." and not "" && !parts[^1].EndsWith(':'))
                    parts.RemoveAt(parts.Count - 1);
                else
                    parts.Add(part);
            }
            else if (part is not "." and not "")
            {
                parts.Add(part);
            }
        }
        return string.Join('/', parts);
    }

    static bool? Bool(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" : null;

    static float? Float(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null;

    static ScaleType? Scale(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) ? v.ToLowerInvariant() switch
        {
            "source" => ScaleType.Source,
            "viewport" => ScaleType.Viewport,
            "absolute" => ScaleType.Absolute,
            _ => throw new ShaderException($"Unknown {key} '{v}'"),
        } : null;

    static WrapMode? Wrap(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) ? v.ToLowerInvariant() switch
        {
            "clamp_to_edge" => WrapMode.ClampToEdge,
            "repeat" => WrapMode.Repeat,
            "mirrored_repeat" => WrapMode.MirroredRepeat,
            _ => WrapMode.ClampToBorder,
        } : null;
}

/// <summary>A shader or preset that can't be used, with a message for the player.</summary>
sealed class ShaderException(string message) : Exception(message);
