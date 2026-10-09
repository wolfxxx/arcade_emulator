namespace Arcade.App;

/// <summary>
/// Data folders (cores, dats, system, saves) live in the repo root during development and next to
/// the executable when distributed.
/// </summary>
sealed record AppPaths(string Root)
{
    public string Cores => Path.Combine(Root, "cores");
    public string Dats => Path.Combine(Root, "dats");
    public string System => Path.Combine(Root, "system");
    /// <summary>Where cores keep NVRAM, high scores and their own settings.</summary>
    public string Saves { get; init; } = Path.Combine(Root, "saves");
    public string Roms => Path.Combine(Root, "roms");
    public string Artwork => Path.Combine(Root, "artwork");
    public string LibraryDb => Path.Combine(Root, "library.db");

    public static AppPaths Discover()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "cores")))
                    return new AppPaths(dir.FullName);
        return new AppPaths(AppContext.BaseDirectory);
    }
}
