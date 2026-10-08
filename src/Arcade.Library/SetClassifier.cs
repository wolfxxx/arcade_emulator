namespace Arcade.Library;

/// <summary>A core the library can assign games to, with its DAT and the folder it loads samples from.</summary>
public sealed record LibraryCore(string Id, string DisplayName, DatFile Dat, string? SamplesDir);

public enum GameStatus
{
    /// <summary>A core has every file it needs.</summary>
    Playable,
    /// <summary>Only a BIOS zip (e.g. neogeo.zip) is missing.</summary>
    NeedsBios,
    /// <summary>A clone whose parent zip is missing.</summary>
    NeedsParent,
    /// <summary>The zip contains files from another version of the set than any installed core expects.</summary>
    WrongVersion,
    /// <summary>Some ROM files are missing.</summary>
    MissingFiles,
    /// <summary>No installed core knows a set with this name.</summary>
    Unknown,
    /// <summary>A BIOS or device set: needed by other games but not playable itself.</summary>
    Bios,
}

public sealed record CoreVerdict(LibraryCore Core, RomSetCheck Check);

/// <summary>The outcome for one ROM zip: its status, the core that will run it, and why it might not work well.</summary>
public sealed record SetAssessment(
    string SetName,
    GameStatus Status,
    LibraryCore? Core,
    RomSetCheck? Check,
    IReadOnlyList<CoreVerdict> Verdicts,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The DAT entry the metadata comes from: the chosen core's, else the first core that knows the set.</summary>
    public DatGame? Game => Check?.Game ?? Verdicts.Select(v => v.Check.Game).FirstOrDefault(g => g != null);

    /// <summary>One line explaining a non-playable status.</summary>
    public string? Problem => Status switch
    {
        GameStatus.Playable or GameStatus.Bios => null,
        GameStatus.Unknown => "no installed core knows this set",
        _ => $"{Core?.DisplayName}: {Check?.Describe()}",
    };
}

/// <summary>
/// Decides which core runs a ROM set, using every installed core's DAT. Cores are tried in preference
/// order; the first with a complete set wins. When none is complete, the most useful explanation is kept
/// (a missing BIOS or parent beats a long list of missing files).
/// </summary>
public static class SetClassifier
{
    public static SetAssessment Assess(
        string romPath,
        IReadOnlyList<LibraryCore> cores,
        IReadOnlyList<string> extraSearchDirs,
        Func<string, IReadOnlyCollection<uint>?>? readCrcs = null,
        string? preferredCoreId = null)
    {
        readCrcs ??= RomSetChecker.ReadCrcs;
        var setName = Path.GetFileNameWithoutExtension(romPath);
        var verdicts = cores
            .Select(core => new CoreVerdict(core, RomSetChecker.Check(core.Dat, romPath, extraSearchDirs, readCrcs)))
            .ToList();
        var known = verdicts.Where(v => v.Check.Status != RomSetStatus.UnknownSet).ToList();

        if (known.Count == 0)
            return new SetAssessment(setName, GameStatus.Unknown, null, null, verdicts, []);

        if (known.Any(v => v.Check.Game!.IsBios))
        {
            var bios = known.First(v => v.Check.Game!.IsBios);
            return new SetAssessment(setName, GameStatus.Bios, bios.Core, bios.Check, verdicts, []);
        }

        var preferred = preferredCoreId == null ? null : known.FirstOrDefault(v => v.Core.Id.Equals(preferredCoreId, StringComparison.OrdinalIgnoreCase));
        var chosen = preferred
            ?? known.FirstOrDefault(v => v.Check.Status is RomSetStatus.Complete or RomSetStatus.Unverifiable)
            ?? known.OrderBy(v => v.Check.MissingAncestorOnly != null ? 0 : 1)
                    .ThenBy(v => v.Check.ForeignFiles > 0 ? 1 : 0)
                    .ThenBy(v => v.Check.Missing.Count)
                    .First();

        var check = chosen.Check;
        var status = check.Status switch
        {
            RomSetStatus.Complete or RomSetStatus.Unverifiable => GameStatus.Playable,
            _ when check.MissingAncestorOnly is { } ancestor => ancestor.IsBios ? GameStatus.NeedsBios : GameStatus.NeedsParent,
            _ when check.ForeignFiles > 0 => GameStatus.WrongVersion,
            _ => GameStatus.MissingFiles,
        };

        return new SetAssessment(setName, status, chosen.Core, check, verdicts, Warnings(chosen.Core, check.Game!));
    }

    static List<string> Warnings(LibraryCore core, DatGame game)
    {
        var warnings = new List<string>();
        if (game.Status == DriverStatus.Preliminary)
            warnings.Add($"{core.DisplayName} marks this game as not working");
        else if (game.Status == DriverStatus.Imperfect)
            warnings.Add($"{core.DisplayName} emulates this game imperfectly");

        if (game.Samples.Count > 0 && core.SamplesDir != null && !File.Exists(Path.Combine(core.SamplesDir, game.SamplesZipName + ".zip")))
            warnings.Add($"some sounds need samples: {game.SamplesZipName}.zip in {core.SamplesDir}");
        return warnings;
    }
}
