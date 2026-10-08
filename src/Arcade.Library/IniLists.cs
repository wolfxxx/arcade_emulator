namespace Arcade.Library;

/// <summary>
/// Reads the community "set=value" lists such as catver.ini (genre per set, section [Category]) and
/// nplayers.ini (section [NPlayers]). These are optional extras users drop into the dats folder.
/// </summary>
public static class IniLists
{
    public static Dictionary<string, string> Load(string path, string section) =>
        File.Exists(path) ? Parse(File.ReadLines(path), section) : new(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string> Parse(IEnumerable<string> lines, string section)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;
            if (line[0] == '[')
            {
                inSection = line.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (inSection && eq > 0)
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return values;
    }
}
