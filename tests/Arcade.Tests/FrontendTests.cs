using Arcade.App;
using Arcade.App.Browser;
using Arcade.App.Ui;
using Arcade.Library;

namespace Arcade.Tests;

public class GameListModelTests
{
    static LibraryGame Game(string set, string title, GameStatus status = GameStatus.Playable, string? year = null, bool favorite = false,
        Orientation orientation = Orientation.Horizontal, string? parent = null, int plays = 0, DateTime? lastPlayed = null, string[]? warnings = null) =>
        new(set, $"{set}.zip", title, year, "Maker", parent, orientation, 2, null, null, status, "fbneo", null, warnings ?? [], null, favorite, plays, lastPlayed);

    static GameListModel Model(params LibraryGame[] games)
    {
        var model = new GameListModel();
        model.SetGames(games);
        return model;
    }

    static readonly LibraryGame[] Sample =
    [
        Game("robby", "The Adventures of Robby Roto!", year: "1981", favorite: true, plays: 3, lastPlayed: new DateTime(2026, 10, 1)),
        Game("alienar", "Alien Arena", year: "1985"),
        Game("gridlee", "Gridlee", year: "1983", warnings: ["needs samples"]),
        Game("supertnk", "Super Tank", year: "1981", orientation: Orientation.Vertical, plays: 1, lastPlayed: new DateTime(2026, 10, 5)),
        Game("sf2", "Street Fighter II", status: GameStatus.MissingFiles),
        Game("sf2ua", "Street Fighter II (US rev A)", parent: "sf2"),
        Game("1942", "1942", year: "1984"),
    ];

    [Fact]
    public void All_tab_lists_playable_games_by_title_ignoring_a_leading_The()
    {
        var model = Model(Sample);
        Assert.Equal(["1942", "The Adventures of Robby Roto!", "Alien Arena", "Gridlee", "Street Fighter II (US rev A)", "Super Tank"],
            model.Visible.Select(g => g.Title));
    }

    [Fact]
    public void Tabs_filter_by_favourite_recent_orientation_and_problems()
    {
        var model = Model(Sample);
        model.SetTab(BrowserTab.Favorites);
        Assert.Equal(["robby"], model.Visible.Select(g => g.SetName));
        model.SetTab(BrowserTab.Recent);
        Assert.Equal(["supertnk", "robby"], model.Visible.Select(g => g.SetName)); // most recent first
        model.SetTab(BrowserTab.Vertical);
        Assert.Equal(["supertnk"], model.Visible.Select(g => g.SetName));
        model.SetTab(BrowserTab.Attention);
        Assert.Equal(["gridlee", "sf2"], model.Visible.Select(g => g.SetName).Order());
    }

    [Fact]
    public void Search_matches_title_set_name_and_maker_and_clones_can_be_hidden()
    {
        var model = Model(Sample);
        model.SetSearch("fighter");
        Assert.Equal(["sf2ua"], model.Visible.Select(g => g.SetName));
        model.SetSearch("SUPERTNK");
        Assert.Equal(["supertnk"], model.Visible.Select(g => g.SetName));
        model.SetSearch("");
        model.SetShowClones(false);
        Assert.DoesNotContain(model.Visible, g => g.SetName == "sf2ua");
    }

    [Fact]
    public void Selection_follows_the_game_when_the_list_changes()
    {
        var model = Model(Sample);
        Assert.True(model.Select("gridlee"));
        model.SetSort(SortOrder.Year);
        Assert.Equal("gridlee", model.Current!.SetName);
        model.SetTab(BrowserTab.Vertical); // gridlee isn't listed: selection stays in range
        Assert.Equal("supertnk", model.Current!.SetName);
    }

    [Fact]
    public void Moving_wraps_by_one_but_pages_stop_at_the_ends()
    {
        var model = Model(Sample);
        model.Move(-1);
        Assert.Equal(model.Visible.Count - 1, model.Selected);
        model.Move(1);
        Assert.Equal(0, model.Selected);
        model.Move(-10);
        Assert.Equal(0, model.Selected);
        model.Move(100);
        Assert.Equal(model.Visible.Count - 1, model.Selected);
    }

    [Fact]
    public void Letter_jumps_go_to_the_next_and_previous_letter_groups()
    {
        var model = Model(Sample); // 1942 | Adventures, Alien | Gridlee | Street…, Super…
        Assert.Equal('#', GameListModel.GroupKey(model.Current!));
        model.JumpLetter(1);
        Assert.Equal("robby", model.Current!.SetName); // "The Adventures…" files under A
        model.JumpLetter(1);
        Assert.Equal("gridlee", model.Current!.SetName);
        model.JumpLetter(1);
        Assert.Equal("sf2ua", model.Current!.SetName);
        model.Move(1);
        model.JumpLetter(-1);
        Assert.Equal("sf2ua", model.Current!.SetName); // start of the current group first
        model.JumpLetter(-1);
        Assert.Equal("gridlee", model.Current!.SetName);
    }

    [Fact]
    public void Tabs_cycle_in_both_directions()
    {
        var model = Model(Sample);
        model.NextTab(-1);
        Assert.Equal(BrowserTab.Attention, model.Tab);
        model.NextTab(1);
        Assert.Equal(BrowserTab.All, model.Tab);
    }
}

public class UiPrimitiveTests
{
    [Fact]
    public void Parses_theme_colours()
    {
        Assert.Equal(new Rgba(0xff, 0x3d, 0x7f, 0xff), Rgba.Parse("#ff3d7f"));
        Assert.Equal(new Rgba(0x05, 0x06, 0x0c, 0xc8), Rgba.Parse("#05060cc8"));
        Assert.Equal(new Rgba(0xff, 0xaa, 0x00, 0xff), Rgba.Parse("fa0"));
        Assert.Throws<FormatException>(() => Rgba.Parse("#12345"));
    }

    [Fact]
    public void Premultiplied_colour_scales_rgb_by_alpha()
    {
        var packed = new Rgba(200, 100, 50, 128).Premultiplied;
        Assert.Equal((100u, 50u, 25u, 128u), (packed & 0xFF, packed >> 8 & 0xFF, packed >> 16 & 0xFF, packed >> 24));
    }

    [Fact]
    public void Fit_keeps_aspect_and_centres()
    {
        var fit = new RectF(0, 0, 1600, 900).Fit(4f / 3);
        Assert.Equal(new RectF(200, 0, 1200, 900), fit);
        Assert.Equal(new RectF(0, 150, 400, 300), new RectF(0, 0, 400, 600).Fit(4f / 3));
    }

    [Fact]
    public void Bundled_themes_are_valid()
    {
        var themes = Theme.List(Path.Combine(TestPaths.RepoRoot, "themes"));
        Assert.Contains(themes, t => t.Id == "midnight");
        Assert.Contains(themes, t => t.Id == "cabinet");
        foreach (var (id, name) in themes)
        {
            Assert.DoesNotContain("invalid", name);
            var warnings = new List<string>();
            using var theme = Theme.Load(Path.Combine(TestPaths.RepoRoot, "themes"), TestPaths.RepoRoot, id, warnings.Add);
            Assert.Empty(warnings);
            Assert.Equal(id, theme.Id);
        }
    }
}
