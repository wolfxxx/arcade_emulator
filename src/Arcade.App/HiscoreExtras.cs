namespace Arcade.App;

/// <summary>
/// High score entries missing from MAME 2003-Plus's own hiscore.dat. The core writes that file into
/// system/mame2003-plus the first time it runs and never replaces it, so these are appended to it.
/// Each line is cpu:address:length:first byte:last byte, the bytes being the values the game sets
/// when it starts, which tell the core the table is ready to be loaded.
/// </summary>
static class HiscoreExtras
{
    static readonly (string Set, string[] Lines)[] Entries =
    [
        // Super Tank keeps no names, just one high score: a big-endian word counting tens of points.
        ("supertnk", ["0:1bda:2:00:00"]),
    ];

    public static void Apply(string systemDirectory)
    {
        var path = Path.Combine(systemDirectory, "mame2003-plus", "hiscore.dat");
        try
        {
            if (!File.Exists(path))
                return; // the core hasn't written it yet; the next game start adds them
            var text = File.ReadAllText(path);
            var missing = Entries.Where(e => !text.Contains($"\n{e.Set}:", StringComparison.Ordinal)).ToList();
            if (missing.Count == 0)
                return;
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var add = string.Concat(missing.Select(e => $"{nl}{e.Set}:{nl}{string.Join(nl, e.Lines)}{nl}"));
            File.AppendAllText(path, (text.EndsWith('\n') ? "" : nl) + add);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
