using Arcade.Library;

namespace Arcade.App;

/// <param name="SamplesFolder">Where the core looks for sample zips, relative to the system folder.</param>
sealed record CoreDefinition(string Id, string DisplayName, string DllName, string DatName, string SamplesFolder);

sealed record CoreChoice(CoreDefinition? Core, string DllPath, SetAssessment? Assessment)
{
    public RomSetCheck? Check => Assessment?.Check;
    public IReadOnlyList<string> Warnings => Assessment?.Warnings ?? [];
}

/// <summary>Knows the supported cores and picks the right one for a ROM by checking it against each core's DAT.</summary>
sealed class CoreCatalog(AppPaths paths)
{
    // Order is preference: FBNeo is more accurate for the games both cores support.
    public static readonly CoreDefinition[] Known =
    [
        new("fbneo", "FinalBurn Neo", "fbneo_libretro.dll", "fbneo.dat", Path.Combine("fbneo", "samples")),
        new("mame2003_plus", "MAME 2003-Plus", "mame2003_plus_libretro.dll", "mame2003_plus.dat", Path.Combine("mame2003-plus", "samples")),
    ];

    List<LibraryCore>? _libraryCores;

    public static CoreDefinition? Find(string id) => Known.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Installed cores that have a DAT, in preference order: the ones the library can verify games for.</summary>
    public IReadOnlyList<LibraryCore> LibraryCores => _libraryCores ??= Known
        .Where(c => File.Exists(DllPath(c)) && File.Exists(Path.Combine(paths.Dats, c.DatName)))
        .Select(c => new LibraryCore(c.Id, c.DisplayName, DatFile.Load(Path.Combine(paths.Dats, c.DatName)), Path.Combine(paths.System, c.SamplesFolder)))
        .ToList();

    public SetAssessment Assess(string romPath, string? preferredCoreId = null) =>
        SetClassifier.Assess(romPath, LibraryCores, [paths.System], preferredCoreId: preferredCoreId);

    public LibraryScanner CreateScanner(GameLibrary library) => new(library, LibraryCores, [paths.System])
    {
        Genres = IniLists.Load(Path.Combine(paths.Dats, "catver.ini"), "Category"),
        NPlayers = IniLists.Load(Path.Combine(paths.Dats, "nplayers.ini"), "NPlayers"),
    };

    /// <param name="forcedCore">A core id from <see cref="Known"/> or a path to any libretro DLL; null to auto-select.</param>
    public CoreChoice Choose(string romPath, string? forcedCore)
    {
        CoreDefinition? forced = null;
        if (forcedCore != null)
        {
            forced = Find(forcedCore);
            if (forced == null)
                return new CoreChoice(null, Path.GetFullPath(forcedCore), null);
        }

        if (LibraryCores.Count == 0)
        {
            // No DATs to verify against: take the requested or first installed core and hope.
            var fallback = forced ?? Known.FirstOrDefault(c => File.Exists(DllPath(c)))
                ?? throw new InvalidOperationException($"No cores installed in {paths.Cores}. Run tools/fetch-deps.ps1.");
            return new CoreChoice(fallback, DllPath(fallback), null);
        }

        var assessment = Assess(romPath, forced?.Id);
        if (forced != null)
            return new CoreChoice(forced, DllPath(forced), assessment.Core?.Id == forced.Id ? assessment : null);
        if (assessment.Status == GameStatus.Playable)
            return new CoreChoice(Find(assessment.Core!.Id), DllPath(Find(assessment.Core.Id)!), assessment);

        var verdicts = assessment.Verdicts.Select(v => $"{v.Core.DisplayName}: {v.Check.Describe()}");
        var reason = assessment.Status switch
        {
            GameStatus.Bios => $"'{assessment.SetName}' is a BIOS set: other games need it, but it can't be played itself.",
            GameStatus.NeedsBios => "Put the BIOS zip in the ROM folder or in " + paths.System + ".",
            GameStatus.NeedsParent => "This is a clone: put its parent set's zip in the same folder.",
            _ => "Arcade ROM sets must match the version each core expects. Use --core <id> to try anyway.",
        };
        throw new RomSetException(
            $"No installed core can run '{Path.GetFileName(romPath)}'.\n\n" + string.Join("\n", verdicts) + "\n\n" + reason);
    }

    string DllPath(CoreDefinition core) => Path.Combine(paths.Cores, core.DllName);
}

sealed class RomSetException(string message) : Exception(message);
