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
    public string Saves => Path.Combine(Root, "saves");

    public static AppPaths Discover()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "cores")))
                return new AppPaths(dir.FullName);
        return new AppPaths(AppContext.BaseDirectory);
    }
}
