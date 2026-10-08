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

public sealed record RomSetCheck(string SetName, RomSetStatus Status, DatGame? Game, IReadOnlyList<DatRom> Missing, IReadOnlyList<string> RequiredZips)
{
    public string Describe() => Status switch
    {
        RomSetStatus.Complete => "complete",
        RomSetStatus.UnknownSet => $"'{SetName}' is not a set this core knows",
        RomSetStatus.Unverifiable => "contents could not be verified (not a zip)",
        _ => $"missing {Missing.Count} file(s): {string.Join(", ", Missing.Take(8).Select(r => r.Name))}{(Missing.Count > 8 ? ", …" : "")}"
            + (RequiredZips.Count > 1 ? $" (looked in {string.Join(", ", RequiredZips)})" : ""),
    };
}

/// <summary>
/// Checks a ROM zip against a core's DAT by CRC32, the way ROM managers do. Files are matched by
/// CRC rather than name because names differ between set versions while the chip contents don't.
/// Parent sets (clones) and BIOS sets (e.g. neogeo.zip) are looked up next to the ROM and in the system folder.
/// </summary>
public static class RomSetChecker
{
    public static RomSetCheck Check(DatFile dat, string romPath, IEnumerable<string> extraSearchDirs)
    {
        var setName = Path.GetFileNameWithoutExtension(romPath);
        var game = dat.Find(setName);
        if (game == null)
            return new RomSetCheck(setName, RomSetStatus.UnknownSet, null, [], []);
        if (!romPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return new RomSetCheck(setName, RomSetStatus.Unverifiable, game, [], []);

        var searchDirs = new List<string> { Path.GetDirectoryName(Path.GetFullPath(romPath))! };
        searchDirs.AddRange(extraSearchDirs);

        var available = new HashSet<uint>(ReadCrcs(romPath));
        var zips = new List<string> { Path.GetFileName(romPath) };

        // Follow the romof chain: clone -> parent -> BIOS. Guard against cycles in bad DATs.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { game.Name };
        for (var ancestor = game.RomOf; ancestor != null && seen.Add(ancestor); ancestor = dat.Find(ancestor)?.RomOf)
        {
            zips.Add(ancestor + ".zip");
            var zip = searchDirs.Select(d => Path.Combine(d, ancestor + ".zip")).FirstOrDefault(File.Exists);
            if (zip != null)
                available.UnionWith(ReadCrcs(zip));
        }

        var missing = game.Roms
            .Where(r => r is { NoDump: false, Crc: not null } && !available.Contains(r.Crc.Value))
            .DistinctBy(r => r.Crc)
            .ToList();

        return new RomSetCheck(setName, missing.Count == 0 ? RomSetStatus.Complete : RomSetStatus.Incomplete, game, missing, zips);
    }

    static IEnumerable<uint> ReadCrcs(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            // CRCs come from the zip directory, so nothing needs to be decompressed.
            return archive.Entries.Where(e => e.Length > 0).Select(e => e.Crc32).ToList();
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }
}
