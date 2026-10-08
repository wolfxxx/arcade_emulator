namespace Arcade.Library;

public enum ArtworkKind { Snap, Title, Marquee, Flyer, Cabinet }

/// <summary>
/// Finds artwork the user dropped into the artwork folder. Two common pack layouts are supported:
/// MAME-style folders named by set (<c>snap/robby.png</c>) and libretro-thumbnails folders named by title
/// (<c>.../Named_Snaps/The Adventures of Robby Roto!.png</c>). Clones fall back to their parent's artwork.
/// </summary>
public sealed class ArtworkLocator(string root)
{
    static readonly Dictionary<ArtworkKind, string[]> MameFolders = new()
    {
        [ArtworkKind.Snap] = ["snap", "snaps"],
        [ArtworkKind.Title] = ["titles", "title"],
        [ArtworkKind.Marquee] = ["marquees", "marquee"],
        [ArtworkKind.Flyer] = ["flyers", "flyer"],
        [ArtworkKind.Cabinet] = ["cabinets", "cabinet"],
    };

    static readonly Dictionary<ArtworkKind, string> LibretroFolders = new()
    {
        [ArtworkKind.Snap] = "Named_Snaps",
        [ArtworkKind.Title] = "Named_Titles",
        [ArtworkKind.Flyer] = "Named_Boxarts",
    };

    static readonly string[] Extensions = [".png", ".jpg"];

    public string? Find(ArtworkKind kind, string setName, string title, string? parent = null)
    {
        if (!Directory.Exists(root))
            return null;
        foreach (var name in parent == null ? [setName] : new[] { setName, parent })
            foreach (var folder in MameFolders[kind])
                if (FirstExisting(Path.Combine(root, folder, name)) is { } path)
                    return path;

        if (LibretroFolders.TryGetValue(kind, out var named))
            foreach (var system in Directory.EnumerateDirectories(root))
                if (FirstExisting(Path.Combine(system, named, LibretroName(title))) is { } path)
                    return path;
        return null;
    }

    /// <summary>libretro-thumbnails replaces these characters in titles with underscores.</summary>
    public static string LibretroName(string title)
    {
        var chars = title.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is '&' or '*' or '/' or ':' or '`' or '<' or '>' or '?' or '\\' or '|' or '"')
                chars[i] = '_';
        return new string(chars);
    }

    static string? FirstExisting(string pathWithoutExtension) =>
        Extensions.Select(ext => pathWithoutExtension + ext).FirstOrDefault(File.Exists);
}
