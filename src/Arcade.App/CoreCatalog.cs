using Arcade.Library;

namespace Arcade.App;

sealed record CoreDefinition(string Id, string DisplayName, string DllName, string DatName);

sealed record CoreChoice(CoreDefinition? Core, string DllPath, RomSetCheck? Check);

/// <summary>Knows the supported cores and picks the right one for a ROM by checking it against each core's DAT.</summary>
sealed class CoreCatalog(AppPaths paths)
{
    // Order is preference: FBNeo is more accurate for the games both cores support.
    public static readonly CoreDefinition[] Known =
    [
        new("fbneo", "FinalBurn Neo", "fbneo_libretro.dll", "fbneo.dat"),
        new("mame2003_plus", "MAME 2003-Plus", "mame2003_plus_libretro.dll", "mame2003_plus.dat"),
    ];

    readonly Dictionary<string, DatFile?> _dats = new();

    /// <param name="forcedCore">A core id from <see cref="Known"/> or a path to any libretro DLL; null to auto-select.</param>
    public CoreChoice Choose(string romPath, string? forcedCore)
    {
        if (forcedCore != null)
        {
            var known = Known.FirstOrDefault(c => c.Id.Equals(forcedCore, StringComparison.OrdinalIgnoreCase));
            if (known == null)
                return new CoreChoice(null, Path.GetFullPath(forcedCore), null);
            var check = LoadDat(known) is { } forcedDat ? RomSetChecker.Check(forcedDat, romPath, [paths.System]) : null;
            return new CoreChoice(known, DllPath(known), check);
        }

        var verdicts = new List<string>();
        foreach (var core in Known)
        {
            if (!File.Exists(DllPath(core)))
            {
                verdicts.Add($"{core.DisplayName}: core not installed");
                continue;
            }
            var dat = LoadDat(core);
            if (dat == null)
            {
                // Without a DAT we can't verify; take the first installed core and hope.
                return new CoreChoice(core, DllPath(core), null);
            }
            var check = RomSetChecker.Check(dat, romPath, [paths.System]);
            if (check.Status is RomSetStatus.Complete or RomSetStatus.Unverifiable)
                return new CoreChoice(core, DllPath(core), check);
            verdicts.Add($"{core.DisplayName}: {check.Describe()}");
        }

        throw new RomSetException(
            $"No installed core can run '{Path.GetFileName(romPath)}'.\n\n" + string.Join("\n", verdicts) +
            "\n\nArcade ROM sets must match the version each core expects. Use --core <id> to try anyway.");
    }

    string DllPath(CoreDefinition core) => Path.Combine(paths.Cores, core.DllName);

    DatFile? LoadDat(CoreDefinition core)
    {
        if (!_dats.TryGetValue(core.Id, out var dat))
        {
            var path = Path.Combine(paths.Dats, core.DatName);
            dat = File.Exists(path) ? DatFile.Load(path) : null;
            _dats[core.Id] = dat;
        }
        return dat;
    }
}

sealed class RomSetException(string message) : Exception(message);
