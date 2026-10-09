using Arcade.Library;

namespace Arcade.App;

/// <summary>
/// Command-line access to the game library until the on-screen browser exists (Phase 3):
/// scan folders, list and inspect games, and set per-game choices.
/// </summary>
sealed class LibraryCommands(AppPaths paths)
{
    readonly CoreCatalog _catalog = new(paths);

    public static readonly string[] Names = ["scan", "list", "info", "set-core", "favorite", "folders", "bezels", "update-cores"];

    public int Run(string command, string[] args)
    {
        using var library = new GameLibrary(paths.LibraryDb);
        return command switch
        {
            "scan" => Scan(library, args),
            "list" => List(library, args),
            "info" when args.Length == 1 => Info(library, args[0]),
            "set-core" when args.Length == 2 => SetCore(library, args[0], args[1]),
            "favorite" when args.Length is 1 or 2 => Favorite(library, args[0], args.Length == 1 || args[1] != "off"),
            "folders" => Folders(library, args),
            "bezels" => Bezels(library),
            "update-cores" => UpdateCores(library),
            _ => Fail($"Wrong arguments for '{command}'. Run with --help for usage."),
        };
    }

    /// <summary>Installs or updates the cores and their game lists, then rescans if a list changed.</summary>
    int UpdateCores(GameLibrary library)
    {
        var result = new CoreUpdater(paths).UpdateAsync(Console.WriteLine).GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine(result.Updated.Count == 0 ? "Nothing new." : $"Updated: {string.Join(", ", result.Updated)}");
        foreach (var problem in result.Problems)
            Console.WriteLine("  problem: " + problem);
        if ((result.ListsChanged || result.CoresChanged) && library.Folders.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Checking the library against the new cores…");
            return Scan(library, []) is var code && result.Problems.Count == 0 ? code : 1;
        }
        return result.Problems.Count == 0 ? 0 : 1;
    }

    /// <summary>Downloads The Bezel Project's artwork for every playable game that has none yet.</summary>
    int Bezels(GameLibrary library)
    {
        var games = library.Games();
        if (games.Count == 0)
            return Fail("The library is empty. Run: scan <folder>");
        Console.WriteLine($"Looking for bezels for {games.Count} game(s) at The Bezel Project…");
        var result = new Video.BezelDownloader(paths.Artwork)
            .DownloadAsync(games.Select(g => (g.SetName, g.Parent)), Console.WriteLine).GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine($"{result.Downloaded} picture(s) downloaded to {Path.Combine(paths.Artwork, "bezels")}; "
            + $"{result.AlreadyHad} game(s) already had one; {result.NotAvailable} have none there (they use the general bezel).");
        foreach (var problem in result.Problems)
            Console.WriteLine("  problem: " + problem);
        return result.Problems.Count == 0 ? 0 : 1;
    }

    int Scan(GameLibrary library, string[] folders)
    {
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
                return Fail($"Folder not found: {folder}");
            library.AddFolder(folder);
        }
        if (library.Folders.Count == 0)
        {
            if (!Directory.Exists(paths.Roms))
                return Fail("No ROM folders yet. Run: scan <folder>");
            library.AddFolder(paths.Roms);
        }
        if (_catalog.LibraryCores.Count == 0)
            return Fail($"No cores with DATs found in {paths.Cores} and {paths.Dats}. Run tools/fetch-deps.ps1.");

        Console.WriteLine($"Cores: {string.Join(", ", _catalog.LibraryCores.Select(c => c.DisplayName))}");
        var summary = _catalog.CreateScanner(library).Scan(new Progress<string>(Console.WriteLine));

        Console.WriteLine();
        Console.WriteLine($"{summary[GameStatus.Playable]} playable game(s) found in {summary.Elapsed.TotalSeconds:F1} s.");
        foreach (var status in Enum.GetValues<GameStatus>().Where(s => s is not GameStatus.Playable && summary[s] > 0))
            Console.WriteLine($"  {summary[status],5}  {Label(status)}");

        var problems = library.Games(new LibraryQuery(IncludeUnplayable: true))
            .Where(g => g.Status is not (GameStatus.Playable or GameStatus.Bios) || g.Warnings.Count > 0)
            .ToList();
        if (problems.Count > 0)
        {
            Console.WriteLine();
            foreach (var g in problems.Take(30))
                Console.WriteLine($"  {g.SetName,-12} {string.Join("; ", g.Problem is { } p ? [p, .. g.Warnings] : g.Warnings)}");
            if (problems.Count > 30)
                Console.WriteLine($"  … and {problems.Count - 30} more (see: list --all)");
        }
        return 0;
    }

    static int List(GameLibrary library, string[] args)
    {
        var all = args.Contains("--all");
        var search = string.Join(' ', args.Where(a => !a.StartsWith("--")));
        var games = library.Games(new LibraryQuery(search, IncludeUnplayable: all, FavoritesOnly: args.Contains("--favorites")));
        if (games.Count == 0)
        {
            Console.WriteLine(library.Folders.Count == 0 ? "The library is empty. Run: scan <folder>" : "No matching games.");
            return 0;
        }

        Console.WriteLine($"{"Set",-12} {"Title",-44} {"Year",-4} {"Manufacturer",-22} {"Core",-13} Notes");
        foreach (var g in games)
        {
            var notes = new List<string>();
            if (g.Favorite) notes.Add("★");
            if (g.Orientation == Orientation.Vertical) notes.Add("vertical");
            if (g.Status != GameStatus.Playable) notes.Add(Label(g.Status));
            if (g.Warnings.Count > 0) notes.Add("! " + g.Warnings[0]);
            Console.WriteLine($"{g.SetName,-12} {Cut(g.Title, 44),-44} {g.Year,-4} {Cut(g.Manufacturer ?? "", 22),-22} {g.CoreOverride ?? g.CoreId,-13} {string.Join(", ", notes)}");
        }
        Console.WriteLine($"\n{games.Count} game(s).{(all ? "" : " Use --all to include ones that can't be played.")}");
        return 0;
    }

    int Info(GameLibrary library, string setName)
    {
        var game = library.Find(setName);
        if (game == null)
            return Fail($"'{setName}' is not in the library. Run scan first, or check: list --all");

        Console.WriteLine($"{game.Title}  [{game.SetName}]");
        Console.WriteLine($"  {string.Join(" · ", new[] { game.Year, game.Manufacturer, game.Genre }.Where(s => s != null))}");
        Console.WriteLine($"  File:     {game.Path}");
        if (game.Parent != null) Console.WriteLine($"  Clone of: {game.Parent}");
        Console.WriteLine($"  Screen:   {game.Orientation.ToString().ToLowerInvariant()}{(game.Players is { } p ? $", {p} player(s)" : "")}{(game.Control != null ? $", {game.Control}" : "")}");
        Console.WriteLine($"  Status:   {Label(game.Status)}{(game.Problem != null ? $" — {game.Problem}" : "")}");
        Console.WriteLine($"  Core:     {game.CoreId ?? "none"}{(game.CoreOverride != null ? $" (set by you: {game.CoreOverride})" : " (automatic)")}");
        foreach (var w in game.Warnings)
            Console.WriteLine($"  Note:     {w}");
        Console.WriteLine($"  Played:   {game.PlayCount} time(s), {Browser.BrowserScene.Duration(game.PlayTime)}{(game.LastPlayed is { } t ? $", last {t.ToLocalTime():g}" : "")}");

        if (File.Exists(game.Path))
        {
            Console.WriteLine("  Per core:");
            foreach (var v in _catalog.Assess(game.Path).Verdicts)
                Console.WriteLine($"    {v.Core.DisplayName,-15} {v.Check.Describe()}");
        }

        var art = new ArtworkLocator(paths.Artwork);
        foreach (var kind in Enum.GetValues<ArtworkKind>())
            if (art.Find(kind, game.SetName, game.Title, game.Parent) is { } file)
                Console.WriteLine($"  {kind + ":",-9} {file}");
        return 0;
    }

    int SetCore(GameLibrary library, string setName, string core)
    {
        if (library.Find(setName) == null)
            return Fail($"'{setName}' is not in the library.");
        string? id = core == "auto" ? null : CoreCatalog.Find(core)?.Id;
        if (core != "auto" && id == null)
            return Fail($"Unknown core '{core}'. Use one of: {string.Join(", ", CoreCatalog.Known.Select(c => c.Id))}, auto.");
        library.SetCoreOverride(setName, id);
        Console.WriteLine(id == null ? $"{setName}: core chosen automatically." : $"{setName}: will use {id}.");
        // Re-assess so the stored status reflects the chosen core.
        _catalog.CreateScanner(library).Scan();
        return Info(library, setName);
    }

    static int Favorite(GameLibrary library, string setName, bool on)
    {
        if (library.Find(setName) == null)
            return Fail($"'{setName}' is not in the library.");
        library.SetFavorite(setName, on);
        Console.WriteLine(on ? $"★ {setName} added to favourites." : $"{setName} removed from favourites.");
        return 0;
    }

    static int Folders(GameLibrary library, string[] args)
    {
        if (args is ["--remove", var folder])
        {
            Console.WriteLine(library.RemoveFolder(folder) ? $"Removed {folder}. Run scan to update the library." : $"{folder} was not a library folder.");
            return 0;
        }
        foreach (var f in library.Folders)
            Console.WriteLine($"{f}{(Directory.Exists(f) ? "" : "  (missing)")}");
        return 0;
    }

    public static string Label(GameStatus status) => status switch
    {
        GameStatus.Playable => "playable",
        GameStatus.NeedsBios => "needs BIOS",
        GameStatus.NeedsParent => "needs parent set",
        GameStatus.WrongVersion => "wrong version",
        GameStatus.MissingFiles => "missing files",
        GameStatus.Unknown => "unknown set",
        GameStatus.Bios => "BIOS (not a game)",
        _ => status.ToString(),
    };

    static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
