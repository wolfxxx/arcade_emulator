using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Arcade.App.Video;

/// <summary>A setting a shader offers (<c>#pragma parameter</c>), e.g. scanline strength.</summary>
sealed record ShaderParameter(string Name, string Description, float Default, float Min, float Max, float Step);

/// <summary>
/// One shader file in RetroArch's GLSL format: vertex and fragment stages in one file, selected
/// with <c>#if defined(VERTEX)</c> / <c>FRAGMENT</c>. Both stages are compiled as GLSL 3.30 core;
/// the format's compatibility macros (<c>COMPAT_TEXTURE</c> and so on) pick the modern spellings.
/// </summary>
sealed partial class ShaderSource
{
    public string Path { get; }
    public string Text { get; }
    public IReadOnlyList<ShaderParameter> Parameters { get; }

    public ShaderSource(string path, string text)
    {
        Path = path;
        Text = text;
        Parameters = ReadParameters(text);
    }

    /// <summary>The source to compile for one stage.</summary>
    /// <param name="compatibility">
    /// The context accepts older GLSL: compile as the file asks, like RetroArch — its own <c>#version</c>
    /// moved to the top, or none (GLSL 1.10). Otherwise everything is compiled as GLSL 3.30 core.
    /// </param>
    public string Stage(bool vertex, bool compatibility = false)
    {
        var body = new StringBuilder();
        var extensions = new StringBuilder();
        string? version = null;
        foreach (var line in Text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#version", StringComparison.Ordinal))
            {
                version ??= trimmed.TrimEnd('\r');
                body.Append('\n'); // keep line numbers in error messages
                continue;
            }
            if (trimmed.StartsWith("#extension", StringComparison.Ordinal))
            {
                extensions.Append(line.TrimEnd('\r')).Append('\n');
                body.Append('\n');
                continue;
            }
            body.Append(line.TrimEnd('\r')).Append('\n');
        }
        var header = compatibility ? (version == null ? "" : version + "\n") : "#version 330 core\n";
        return $"{header}{extensions}#define {(vertex ? "VERTEX" : "FRAGMENT")}\n#define PARAMETER_UNIFORM\n#line 1\n{body}";
    }

    // #pragma parameter NAME "Description" default min max [step]
    [GeneratedRegex("""^\s*#pragma\s+parameter\s+(\w+)\s+"([^"]*)"\s+([-+.\deE]+)\s+([-+.\deE]+)\s+([-+.\deE]+)(?:\s+([-+.\deE]+))?""", RegexOptions.Multiline)]
    private static partial Regex ParameterPattern();

    static List<ShaderParameter> ReadParameters(string text)
    {
        var list = new List<ShaderParameter>();
        foreach (Match m in ParameterPattern().Matches(text))
        {
            static float F(Group g, float fallback) =>
                g.Success && float.TryParse(g.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            var min = F(m.Groups[4], 0);
            var max = F(m.Groups[5], 1);
            var step = F(m.Groups[6], 0);
            if (step <= 0)
                step = (max - min) / 20;
            if (list.All(p => p.Name != m.Groups[1].Value))
                list.Add(new ShaderParameter(m.Groups[1].Value, m.Groups[2].Value.Trim(), F(m.Groups[3], 0), min, max, step));
        }
        return list;
    }
}
