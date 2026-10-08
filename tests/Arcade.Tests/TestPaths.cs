// libretro cores are process-wide singletons, so tests that load a core must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Arcade.Tests;

static class TestPaths
{
    public static readonly string RepoRoot = FindRepoRoot();

    public static string Core(string name) => Path.Combine(RepoRoot, "cores", name + "_libretro.dll");
    public static string Rom(string name) => Path.Combine(RepoRoot, "roms", name + ".zip");

    static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ArcadeEmulator.sln")))
                return dir.FullName;
        throw new InvalidOperationException("Could not locate ArcadeEmulator.sln above the test output folder.");
    }
}

/// <summary>A test that needs a downloaded core and ROM; skipped (not failed) when tools/fetch-deps.ps1 hasn't run.</summary>
sealed class RequiresCoreFactAttribute : FactAttribute
{
    public RequiresCoreFactAttribute(string core, string rom)
    {
        if (!File.Exists(TestPaths.Core(core)) || !File.Exists(TestPaths.Rom(rom)))
            Skip = $"Needs cores/{core}_libretro.dll and roms/{rom}.zip — run tools/fetch-deps.ps1.";
    }
}
