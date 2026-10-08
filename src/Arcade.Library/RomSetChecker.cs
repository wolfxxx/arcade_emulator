using System.IO.Compression;

namespace Arcade.Library;

public enum RomSetStatus
{
    /// <summary>Every dumped ROM the core needs was found (in the zip, its parent, or its BIOS).</summary>
    Complete,
    /// <summary>The core knows this set, but some ROM files are missing or are a different version.</summary>
    Incomplete,
    /// <summary>The core's DAT has no set with this name.</summary>
    UnknownSet,
    /// <summary>The file isn't a zip, so its contents couldn't be checked.</summary>
    Unverifiable,
}

/// <summary>Missing ROMs grouped by the set they belong to: the game itself, its parent, or a BIOS.</summary>
public sealed record MissingGroup(string SetName, bool IsBios, bool ZipFound, IReadOnlyList<DatRom> Roms);

public sealed record RomSetCheck(string SetName, RomSetStatus Status, DatGame? Game, IReadOnlyList<DatRom> Missing, IReadOnlyList<string> RequiredZips)
{
    public IReadOnlyList<MissingGroup> MissingGroups { get; init; } = [];

    /// <summary>Files in the set's own zip that match no ROM of this set or its parents: a sign of a different set version.</summary>
    public int ForeignFiles { get; init; }

    /// <summary>The only thing missing is a whole parent or BIOS zip that isn't present.</summary>
    public MissingGroup? MissingAncestorOnly =>
        MissingGroups is [{ ZipFound: false } only] && !only.SetName.Equals(SetName, StringComparison.OrdinalIgnoreCase) ? only : null;

    public string Describe()
    {
        switch (Status)
        {
            case RomSetStatus.Complete: return "complete";
            case RomSetStatus.UnknownSet: return $"'{SetName}' is not a set this core knows";
            case RomSetStatus.Unverifiable: return "contents could not be verified (not a zip)";
        }

        var parts = MissingGroups.Select(g =>
            !g.ZipFound && !g.SetName.Equals(SetName, StringComparison.OrdinalIgnoreCase)
                ? $"needs {(g.IsBios ? "BIOS" : "parent set")} {g.SetName}.zip"
                : $"missing {g.Roms.Count} file(s){(g.SetName.Equals(SetName, StringComparison.OrdinalIgnoreCase) ? "" : $" from {g.SetName}.zip")}: "
                  + string.Join(", ", g.Roms.Take(6).Select(r => r.Name)) + (g.Roms.Count > 6 ? ", …" : ""));
        var text = string.Join("; ", parts);
        if (ForeignFiles > 0)
            text += $" ({ForeignFiles} file(s) in the zip are from a different version)";
        return text;
    }
}

/// <summary>
/// Checks a ROM zip against a core's DAT by CRC32, the way ROM managers do. Files are matched by
/// CRC rather than name because names differ between set versions while the chip contents don't.
/// Parent sets (clones) and BIOS sets (e.g. neogeo.zip) are looked up next to the ROM and in the extra folders.
/// </summary>
public static class RomSetChecker
{
    public static RomSetCheck Check(DatFile dat, string romPath, IEnumerable<string> extraSearchDirs) =>
        Check(dat, romPath, extraSearchDirs, ReadCrcs);

    /// <param name="readCrcs">Returns a zip's entry CRCs, or null if the file doesn't exist; lets a library reuse cached CRCs.</param>
    public static RomSetCheck Check(DatFile dat, string romPath, IEnumerable<string> extraSearchDirs, Func<string, IReadOnlyCollection<uint>?> readCrcs)
    {
        var setName = Path.GetFileNameWithoutExtension(romPath);
        var game = dat.Find(setName);
        if (game == null)
            return new RomSetCheck(setName, RomSetStatus.UnknownSet, null, [], []);
        if (!romPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return new RomSetCheck(setName, RomSetStatus.Unverifiable, game, [], []);

        var searchDirs = new List<string> { Path.GetDirectoryName(Path.GetFullPath(romPath))! };
        searchDirs.AddRange(extraSearchDirs);

        var own = readCrcs(romPath) ?? [];
        var available = new HashSet<uint>(own);
        var zips = new List<string> { Path.GetFileName(romPath) };

        // Follow the romof chain: clone -> parent -> BIOS. Guard against cycles in bad DATs.
        var ancestors = new List<(DatGame Game, bool Found)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { game.Name };
        for (var name = game.RomOf; name != null && seen.Add(name); name = dat.Find(name)?.RomOf)
        {
            zips.Add(name + ".zip");
            IReadOnlyCollection<uint>? crcs = null;
            foreach (var dir in searchDirs)
                if ((crcs = readCrcs(Path.Combine(dir, name + ".zip"))) != null)
                    break;
            if (crcs != null)
                available.UnionWith(crcs);
            if (dat.Find(name) is { } ancestor)
                ancestors.Add((ancestor, crcs != null));
        }

        var missing = game.Roms
            .Where(r => r is { NoDump: false, Crc: not null } && !available.Contains(r.Crc.Value))
            .DistinctBy(r => r.Crc)
            .ToList();

        // A missing ROM belongs to the outermost ancestor (BIOS before parent) that also lists it.
        var groups = missing
            .GroupBy(r => ancestors.LastOrDefault(a => a.Game.Roms.Any(ar => ar.Crc == r.Crc)) is { Game: not null } owner
                ? (owner.Game.Name, owner.Game.IsBios, owner.Found)
                : (game.Name, false, true))
            .Select(g => new MissingGroup(g.Key.Item1, g.Key.Item2, g.Key.Item3, g.ToList()))
            .ToList();

        var known = new HashSet<uint>(game.Roms.Concat(ancestors.SelectMany(a => a.Game.Roms)).Where(r => r.Crc != null).Select(r => r.Crc!.Value));
        var foreign = own.Count(crc => !known.Contains(crc));

        return new RomSetCheck(setName, missing.Count == 0 ? RomSetStatus.Complete : RomSetStatus.Incomplete, game, missing, zips)
        {
            MissingGroups = groups,
            ForeignFiles = foreign,
        };
    }

    /// <summary>Entry CRCs from the zip's central directory (nothing is decompressed); null if the file doesn't exist.</summary>
    public static IReadOnlyCollection<uint>? ReadCrcs(string zipPath)
    {
        if (!File.Exists(zipPath))
            return null;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries.Where(e => e.Length > 0).Select(e => e.Crc32).ToList();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
