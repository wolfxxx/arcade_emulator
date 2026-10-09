using System.Collections.Concurrent;
using System.Diagnostics;

namespace Arcade.Library;

public sealed record ScanSummary(int Zips, int ZipsRead, IReadOnlyDictionary<GameStatus, int> ByStatus, TimeSpan Elapsed)
{
    public int this[GameStatus status] => ByStatus.GetValueOrDefault(status);
}

/// <summary>
/// Rebuilds the library from the ROM folders: reads each zip's CRCs (only for new or changed files),
/// assesses every set against the installed cores, and stores the results with metadata from the DATs.
/// </summary>
public sealed class LibraryScanner(GameLibrary library, IReadOnlyList<LibraryCore> cores, IReadOnlyList<string> systemDirs)
{
    /// <summary>Genre per set, e.g. from catver.ini. Clones fall back to their parent's entry.</summary>
    public IReadOnlyDictionary<string, string> Genres { get; init; } = new Dictionary<string, string>();

    /// <summary>Player info per set, e.g. "2P alt" from nplayers.ini; used when the DAT has no player count.</summary>
    public IReadOnlyDictionary<string, string> NPlayers { get; init; } = new Dictionary<string, string>();

    /// <summary>Core to prefer per set when the player hasn't chosen one (used if that core can play it).</summary>
    public IReadOnlyDictionary<string, string> PreferredCores { get; init; } = new Dictionary<string, string>();

    public ScanSummary Scan(IProgress<string>? progress = null)
    {
        var clock = Stopwatch.StartNew();
        var gameZips = library.Folders.Where(Directory.Exists).SelectMany(ListZips).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var allZips = gameZips.Concat(systemDirs.Where(Directory.Exists).SelectMany(ListZips)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        progress?.Report($"Found {gameZips.Count} zip(s) in {library.Folders.Count} folder(s).");

        // Reuse CRCs for zips whose size and modification time haven't changed.
        var cached = library.LoadZipCache();
        var index = new ConcurrentDictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase);
        var entries = new ConcurrentBag<(string, long, long, uint[])>();
        var read = 0;
        Parallel.ForEach(allZips, path =>
        {
            var info = new FileInfo(path);
            var mtime = info.LastWriteTimeUtc.Ticks;
            if (!cached.TryGetValue(path, out var hit) || hit.Size != info.Length || hit.MTime != mtime)
            {
                hit = (info.Length, mtime, RomSetChecker.ReadCrcs(path)?.ToArray() ?? []);
                Interlocked.Increment(ref read);
            }
            index[path] = hit.Crcs;
            entries.Add((path, hit.Size, hit.MTime, hit.Crcs));
        });
        library.SaveZipCache(entries);
        progress?.Report($"Read {read} new or changed zip(s); {allZips.Count - read} unchanged.");

        IReadOnlyCollection<uint>? ReadCrcs(string path) =>
            index.TryGetValue(Path.GetFullPath(path), out var crcs) ? crcs : RomSetChecker.ReadCrcs(path);

        var overrides = library.CoreOverrides();
        var bySet = new Dictionary<string, LibraryGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in gameZips)
        {
            var setName = Path.GetFileNameWithoutExtension(path);
            var assessment = SetClassifier.Assess(path, cores, systemDirs, ReadCrcs, overrides.GetValueOrDefault(setName) ?? PreferredCores.GetValueOrDefault(setName));
            var game = ToLibraryGame(path, assessment);
            // The same set in two folders: keep the better copy.
            if (!bySet.TryGetValue(setName, out var existing) || game.Status < existing.Status)
                bySet[setName] = game;
        }

        library.ReplaceGames(bySet.Values);
        var byStatus = bySet.Values.GroupBy(g => g.Status).ToDictionary(g => g.Key, g => g.Count());
        return new ScanSummary(gameZips.Count, read, byStatus, clock.Elapsed);
    }

    static IEnumerable<string> ListZips(string folder) =>
        Directory.EnumerateFiles(Path.GetFullPath(folder), "*.zip", SearchOption.TopDirectoryOnly);

    LibraryGame ToLibraryGame(string path, SetAssessment a)
    {
        var game = a.Game;
        // DATs differ in which fields they fill in, so take each from the first core (or parent set) that has it.
        var all = a.Verdicts
            .Where(v => v.Check.Game != null)
            .SelectMany(v => new[] { v.Check.Game, v.Check.Game!.CloneOf is { } parent ? v.Core.Dat.Find(parent) : null })
            .OfType<DatGame>()
            .ToList();
        var orientation = all.Select(g => g.Orientation).FirstOrDefault(o => o != Orientation.Unknown);
        var players = all.Select(g => g.Players).FirstOrDefault(p => p != null) ?? ParsePlayers(Lookup(NPlayers, a.SetName, game?.CloneOf));

        return new LibraryGame(
            a.SetName,
            path,
            game?.Description ?? a.SetName,
            game?.Year,
            game?.Manufacturer,
            game?.CloneOf,
            orientation,
            players,
            all.Select(g => g.Control).FirstOrDefault(c => c != null),
            Lookup(Genres, a.SetName, game?.CloneOf),
            a.Status,
            a.Core?.Id,
            a.Problem,
            a.Warnings,
            CoreOverride: null,
            Favorite: false,
            PlayCount: 0,
            LastPlayed: null);
    }

    static string? Lookup(IReadOnlyDictionary<string, string> map, string set, string? parent) =>
        map.GetValueOrDefault(set) ?? (parent == null ? null : map.GetValueOrDefault(parent));

    static int? ParsePlayers(string? text) =>
        text is { Length: > 0 } && char.IsDigit(text[0]) ? text[0] - '0' : null;
}
