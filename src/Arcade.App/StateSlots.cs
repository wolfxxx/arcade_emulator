using Arcade.Libretro;

namespace Arcade.App;

/// <summary>One save-state slot of a game.</summary>
sealed record StateSlot(int Number, string? StatePath, string ThumbnailPath, DateTime? Saved)
{
    public bool IsEmpty => StatePath == null;
}

/// <summary>
/// A game's numbered save states, each with a picture of the moment it was saved:
/// <c>states/&lt;set&gt;/slot3.state</c> and <c>slot3.png</c>. A state saved by an earlier
/// version (<c>saves/&lt;set&gt;.state</c>) shows up as slot 1 until slot 1 is saved again.
/// </summary>
sealed class StateSlots(string statesDir, string setName, string? legacyPath = null)
{
    public const int Count = 8;

    public string Folder => Path.Combine(statesDir, setName);

    string StateFile(int slot) => Path.Combine(Folder, $"slot{slot}.state");
    string ThumbnailFile(int slot) => Path.Combine(Folder, $"slot{slot}.png");

    string? ExistingState(int slot)
    {
        var path = StateFile(slot);
        if (File.Exists(path))
            return path;
        return slot == 1 && legacyPath != null && File.Exists(legacyPath) ? legacyPath : null;
    }

    public StateSlot Get(int slot)
    {
        var path = ExistingState(slot);
        return new StateSlot(slot, path, ThumbnailFile(slot), path == null ? null : File.GetLastWriteTime(path));
    }

    public IReadOnlyList<StateSlot> All() => Enumerable.Range(1, Count).Select(Get).ToList();

    /// <summary>The slot saved most recently, or null if none is.</summary>
    public int? Newest => All().Where(s => !s.IsEmpty).MaxBy(s => s.Saved)?.Number;

    public void Save(int slot, byte[] state, Rgba32Image? thumbnail)
    {
        Directory.CreateDirectory(Folder);
        // Write beside and swap in, so a crash mid-write never loses the previous save.
        var path = StateFile(slot);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, state);
        File.Move(temp, path, overwrite: true);
        if (thumbnail != null)
            PngEncoder.Save(thumbnail, ThumbnailFile(slot));
        else
            DeleteIfExists(ThumbnailFile(slot));
        if (slot == 1 && legacyPath != null)
            DeleteIfExists(legacyPath);
    }

    public byte[]? Load(int slot) => ExistingState(slot) is { } path ? File.ReadAllBytes(path) : null;

    public void Delete(int slot)
    {
        DeleteIfExists(StateFile(slot));
        DeleteIfExists(ThumbnailFile(slot));
        if (slot == 1 && legacyPath != null)
            DeleteIfExists(legacyPath);
    }

    static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
