using Arcade.Library;

namespace Arcade.App.Browser;

enum BrowserTab { All, Favorites, Recent, Horizontal, Vertical, Attention }

/// <summary>
/// The game list's state without any drawing: which games the current tab, search and sort
/// show, and which one is selected. Selection follows the game (not the row) when the list changes.
/// </summary>
sealed class GameListModel
{
    public static readonly BrowserTab[] Tabs = Enum.GetValues<BrowserTab>();

    IReadOnlyList<LibraryGame> _all = [];
    List<LibraryGame> _visible = [];

    public BrowserTab Tab { get; private set; } = BrowserTab.All;
    public string Search { get; private set; } = "";
    public SortOrder Sort { get; private set; } = SortOrder.Title;
    public bool ShowClones { get; private set; } = true;
    public int Selected { get; private set; }

    public IReadOnlyList<LibraryGame> Visible => _visible;
    public IReadOnlyList<LibraryGame> All => _all;
    public LibraryGame? Current => _visible.Count == 0 ? null : _visible[Selected];
    public int PlayableCount => _all.Count(g => g.Status == GameStatus.Playable);

    public static string TabName(BrowserTab tab) => tab switch
    {
        BrowserTab.All => "All games",
        BrowserTab.Favorites => "Favourites",
        BrowserTab.Recent => "Recently played",
        BrowserTab.Horizontal => "Horizontal",
        BrowserTab.Vertical => "Vertical",
        BrowserTab.Attention => "Needs attention",
        _ => tab.ToString(),
    };

    /// <summary>Replaces the games (after a scan or a change), keeping the selected game if it is still listed.</summary>
    public void SetGames(IReadOnlyList<LibraryGame> games)
    {
        var keep = Current?.SetName;
        _all = games;
        Refresh(keep);
    }

    public void SetTab(BrowserTab tab) { Tab = tab; Refresh(Current?.SetName); }
    public void SetSearch(string search) { Search = search; Refresh(Current?.SetName); }
    public void SetSort(SortOrder sort) { Sort = sort; Refresh(Current?.SetName); }
    public void SetShowClones(bool show) { ShowClones = show; Refresh(Current?.SetName); }

    public void NextTab(int delta)
    {
        var i = Array.IndexOf(Tabs, Tab);
        SetTab(Tabs[(i + delta + Tabs.Length) % Tabs.Length]);
    }

    public bool Select(string setName)
    {
        var i = _visible.FindIndex(g => g.SetName.Equals(setName, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            Selected = i;
        return i >= 0;
    }

    public void SelectIndex(int index) => Selected = _visible.Count == 0 ? 0 : Math.Clamp(index, 0, _visible.Count - 1);

    /// <summary>Moves the selection; moving past either end wraps around only from the very end, like most frontends.</summary>
    public void Move(int delta)
    {
        if (_visible.Count == 0)
            return;
        var target = Selected + delta;
        if (Math.Abs(delta) == 1)
            target = (target + _visible.Count) % _visible.Count;
        Selected = Math.Clamp(target, 0, _visible.Count - 1);
    }

    /// <summary>Jumps to the first game of the next (or previous) letter group in title order.</summary>
    public void JumpLetter(int direction)
    {
        if (_visible.Count == 0)
            return;
        if (Sort != SortOrder.Title)
        {
            Move(direction * 10);
            return;
        }
        var current = GroupKey(_visible[Selected]);
        if (direction > 0)
        {
            var i = _visible.FindIndex(Selected, g => GroupKey(g) != current);
            Selected = i < 0 ? 0 : i;
        }
        else
        {
            // To the start of this group, or of the previous group if already at its start.
            var start = Selected;
            while (start > 0 && GroupKey(_visible[start - 1]) == current) start--;
            if (start == Selected && start > 0)
            {
                var previous = GroupKey(_visible[start - 1]);
                start--;
                while (start > 0 && GroupKey(_visible[start - 1]) == previous) start--;
            }
            else if (start == Selected)
            {
                start = _visible.Count - 1;
                var last = GroupKey(_visible[start]);
                while (start > 0 && GroupKey(_visible[start - 1]) == last) start--;
            }
            Selected = start;
        }
    }

    /// <summary>The letter a game is grouped under: its first letter, or '#' for digits and symbols.</summary>
    public static char GroupKey(LibraryGame game)
    {
        var title = SortTitle(game.Title);
        return title.Length > 0 && char.IsLetter(title[0]) ? char.ToUpperInvariant(title[0]) : '#';
    }

    /// <summary>Sort key ignoring a leading "The " so "The Adventures of Robby Roto!" sorts under A.</summary>
    public static string SortTitle(string title) =>
        title.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? title[4..] : title;

    void Refresh(string? keepSet)
    {
        IEnumerable<LibraryGame> games = _all;
        games = Tab switch
        {
            BrowserTab.Attention => games.Where(g => g.Status is not (GameStatus.Playable or GameStatus.Bios) || g.Warnings.Count > 0),
            _ => games.Where(g => g.Status == GameStatus.Playable),
        };
        games = Tab switch
        {
            BrowserTab.Favorites => games.Where(g => g.Favorite),
            BrowserTab.Recent => games.Where(g => g.LastPlayed != null),
            BrowserTab.Horizontal => games.Where(g => g.Orientation == Orientation.Horizontal),
            BrowserTab.Vertical => games.Where(g => g.Orientation == Orientation.Vertical),
            _ => games,
        };
        if (!ShowClones && Tab != BrowserTab.Attention)
            games = games.Where(g => g.Parent == null);
        if (Search.Trim() is { Length: > 0 } search)
            games = games.Where(g =>
                g.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                g.SetName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (g.Manufacturer?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (g.Genre?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

        var byTitle = StringComparer.OrdinalIgnoreCase;
        _visible = (Tab == BrowserTab.Recent
            ? games.OrderByDescending(g => g.LastPlayed)
            : Sort switch
            {
                SortOrder.Year => games.OrderBy(g => g.Year ?? "9999").ThenBy(g => SortTitle(g.Title), byTitle),
                SortOrder.Manufacturer => games.OrderBy(g => g.Manufacturer ?? "~", byTitle).ThenBy(g => SortTitle(g.Title), byTitle),
                SortOrder.MostPlayed => games.OrderByDescending(g => g.PlayCount).ThenBy(g => SortTitle(g.Title), byTitle),
                SortOrder.RecentlyPlayed => games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => SortTitle(g.Title), byTitle),
                _ => games.OrderBy(g => SortTitle(g.Title), byTitle),
            }).ToList();

        if (keepSet == null || !Select(keepSet))
            Selected = Math.Clamp(Selected, 0, Math.Max(0, _visible.Count - 1));
    }
}
