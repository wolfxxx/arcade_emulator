using System.IO.Compression;
using System.Text;
using Arcade.Library;

namespace Arcade.Tests;

public class LibraryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("arcade-library").FullName;
    readonly string _roms;
    readonly string _system;

    public LibraryTests()
    {
        _roms = Directory.CreateDirectory(Path.Combine(_dir, "roms")).FullName;
        _system = Directory.CreateDirectory(Path.Combine(_dir, "system")).FullName;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    string Zip(string folder, string name, params string[] contents)
    {
        var path = Path.Combine(folder, name + ".zip");
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        for (var i = 0; i < contents.Length; i++)
        {
            using var s = zip.CreateEntry($"f{i}.bin").Open();
            s.Write(Encoding.ASCII.GetBytes(contents[i]));
        }
        return path;
    }

    static string Crc(string content) => System.IO.Hashing.Crc32.HashToUInt32(Encoding.ASCII.GetBytes(content)).ToString("x8");

    static string Rom(string content) => $"""<rom name="{content}.bin" size="{content.Length}" crc="{Crc(content)}"/>""";

    // A small DAT: a BIOS-based game, a parent/clone pair, a game with samples, a not-working game.
    static DatFile TestDat() => DatFile.Load(new MemoryStream(Encoding.UTF8.GetBytes($"""
        <datafile>
          <game name="bios" isbios="yes"><description>BIOS</description>{Rom("BIOS")}</game>
          <game name="biosgame" romof="bios"><description>Bios Game</description><year>1994</year>{Rom("BG")}{Rom("BIOS")}</game>
          <game name="parent"><description>Parent</description><manufacturer>Maker</manufacturer>{Rom("P1")}{Rom("P2")}
            <video orientation="vertical"/><input players="2" control="joy8way" buttons="2"/><driver status="good"/></game>
          <game name="clone" cloneof="parent" romof="parent"><description>Clone</description>{Rom("C1")}{Rom("P2")}</game>
          <game name="sampled"><description>Sampled</description>{Rom("S1")}<sample name="boom"/></game>
          <game name="broken"><description>Broken</description>{Rom("X1")}<driver status="preliminary"/></game>
        </datafile>
        """)));

    LibraryCore Core(string id = "test", DatFile? dat = null) => new(id, "Test Core", dat ?? TestDat(), Path.Combine(_system, "samples"));

    SetAssessment Assess(string zip, params LibraryCore[] cores) =>
        SetClassifier.Assess(zip, cores.Length > 0 ? cores : [Core()], [_system]);

    [Fact]
    public void Parses_metadata_fields()
    {
        var parent = TestDat().Find("parent")!;
        Assert.Equal((Orientation.Vertical, 2, "joy8way", 2, DriverStatus.Good, "Maker"),
            (parent.Orientation, parent.Players, parent.Control, parent.Buttons, parent.Status, parent.Manufacturer));
        Assert.Equal(["boom"], TestDat().Find("sampled")!.Samples);
        Assert.Equal("1994", TestDat().Find("biosgame")!.Year);
    }

    [Fact]
    public void Classifies_each_kind_of_set()
    {
        Assert.Equal(GameStatus.Playable, Assess(Zip(_roms, "parent", "P1", "P2")).Status);
        Assert.Equal(GameStatus.Unknown, Assess(Zip(_roms, "mystery", "?")).Status);
        Assert.Equal(GameStatus.Bios, Assess(Zip(_system, "bios", "BIOS")).Status);
        Assert.Equal(GameStatus.MissingFiles, Assess(Zip(_roms, "sampled")).Status);

        var wrong = Assess(Zip(_roms, "broken", "X1-other-revision"));
        Assert.Equal(GameStatus.WrongVersion, wrong.Status);
        Assert.Contains("different version", wrong.Problem);
    }

    [Fact]
    public void Missing_bios_or_parent_is_reported_as_such()
    {
        var needsBios = Assess(Zip(_roms, "biosgame", "BG"));
        Assert.Equal(GameStatus.NeedsBios, needsBios.Status);
        Assert.Contains("needs BIOS bios.zip", needsBios.Problem);

        Zip(_system, "bios", "BIOS");
        Assert.Equal(GameStatus.Playable, Assess(Path.Combine(_roms, "biosgame.zip")).Status);

        var clone = Zip(_roms, "clone", "C1");
        Assert.Equal(GameStatus.NeedsParent, Assess(clone).Status);
        Zip(_roms, "parent", "P1", "P2");
        Assert.Equal(GameStatus.Playable, Assess(clone).Status);
    }

    [Fact]
    public void Warns_about_missing_samples_and_non_working_drivers()
    {
        Assert.Contains("samples: sampled.zip", Assert.Single(Assess(Zip(_roms, "sampled", "S1")).Warnings));
        Directory.CreateDirectory(Path.Combine(_system, "samples"));
        Zip(Path.Combine(_system, "samples"), "sampled", "boom");
        Assert.Empty(Assess(Path.Combine(_roms, "sampled.zip")).Warnings);

        Assert.Contains("not working", Assert.Single(Assess(Zip(_roms, "broken", "X1")).Warnings));
    }

    [Fact]
    public void Prefers_the_first_complete_core_and_honours_an_override()
    {
        var partial = DatFile.Load(new MemoryStream(Encoding.UTF8.GetBytes(
            $"""<datafile><game name="parent">{Rom("P1")}{Rom("P2")}{Rom("P3")}</game></datafile>""")));
        var zip = Zip(_roms, "parent", "P1", "P2");
        var first = Core("first", partial);
        var second = Core("second");

        Assert.Equal("second", Assess(zip, first, second).Core!.Id); // first core lacks P3
        var forced = SetClassifier.Assess(zip, [first, second], [_system], preferredCoreId: "first");
        Assert.Equal(("first", GameStatus.MissingFiles), (forced.Core!.Id, forced.Status));
    }

    [Fact]
    public void Scan_stores_games_keeps_user_data_and_reuses_cached_crcs()
    {
        Zip(_roms, "parent", "P1", "P2");
        Zip(_roms, "clone", "C1");
        Zip(_roms, "biosgame", "BG");
        Zip(_roms, "notes", "not a rom set");

        using var library = new GameLibrary(Path.Combine(_dir, "library.db"));
        library.AddFolder(_roms);
        var scanner = new LibraryScanner(library, [Core()], [_system])
        {
            Genres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["parent"] = "Shooter / Vertical" },
        };

        var first = scanner.Scan();
        Assert.Equal((4, 4, 2), (first.Zips, first.ZipsRead, first[GameStatus.Playable]));
        Assert.Equal((1, 1), (first[GameStatus.NeedsBios], first[GameStatus.Unknown]));

        var clone = library.Find("CLONE")!;
        Assert.Equal(("Clone", "parent", "Shooter / Vertical", Orientation.Vertical), (clone.Title, clone.Parent, clone.Genre, clone.Orientation));
        Assert.Equal(["Clone", "Parent"], library.Games().Select(g => g.Title));
        Assert.Equal(4, library.Games(new LibraryQuery(IncludeUnplayable: true)).Count);
        Assert.Equal(["Clone"], library.Games(new LibraryQuery("clo")).Select(g => g.Title));

        library.SetFavorite("parent", true);
        library.RecordPlay("parent", DateTime.Now);
        library.RecordPlay("parent", DateTime.Now);
        Zip(_system, "bios", "BIOS");

        var second = scanner.Scan();
        Assert.Equal(1, second.ZipsRead); // only the new BIOS zip was read
        Assert.Equal(3, second[GameStatus.Playable]);
        var parent = library.Find("parent")!;
        Assert.Equal((true, 2), (parent.Favorite, parent.PlayCount));
        Assert.NotNull(parent.LastPlayed);
        Assert.Equal(["Parent"], library.Games(new LibraryQuery(FavoritesOnly: true)).Select(g => g.Title));
    }

    [Fact]
    public void Core_override_is_applied_on_rescan()
    {
        var partial = DatFile.Load(new MemoryStream(Encoding.UTF8.GetBytes(
            $"""<datafile><game name="parent">{Rom("P1")}{Rom("P2")}{Rom("P3")}</game></datafile>""")));
        Zip(_roms, "parent", "P1", "P2");
        using var library = new GameLibrary(Path.Combine(_dir, "library.db"));
        library.AddFolder(_roms);
        var scanner = new LibraryScanner(library, [Core("a", partial), Core("b")], [_system]);

        scanner.Scan();
        Assert.Equal("b", library.Find("parent")!.CoreId);
        library.SetCoreOverride("parent", "a");
        scanner.Scan();
        Assert.Equal(("a", "a", GameStatus.MissingFiles), (library.Find("parent")!.CoreId, library.Find("parent")!.CoreOverride, library.Find("parent")!.Status));
        Assert.Equal("parent", Assert.Single(library.Games(new LibraryQuery(IncludeUnplayable: true))).SetName);
    }

    [Fact]
    public void Ini_lists_read_only_the_requested_section()
    {
        var values = IniLists.Parse(
            ["[FOLDER_SETTINGS]", "RootFolderIcon=mame", "", ";comment", "[Category]", "robby=Maze / Digging", "Gridlee = Ball & Paddle"],
            "Category");
        Assert.Equal(2, values.Count);
        Assert.Equal("Ball & Paddle", values["gridlee"]);
    }

    [Fact]
    public void Artwork_is_found_by_set_parent_or_libretro_title()
    {
        var art = Path.Combine(_dir, "artwork");
        Directory.CreateDirectory(Path.Combine(art, "snap"));
        Directory.CreateDirectory(Path.Combine(art, "MAME", "Named_Titles"));
        File.WriteAllText(Path.Combine(art, "snap", "parent.png"), "");
        File.WriteAllText(Path.Combine(art, "MAME", "Named_Titles", "Robby Roto_ The _Game_.png"), "");
        var locator = new ArtworkLocator(art);

        Assert.EndsWith(Path.Combine("snap", "parent.png"), locator.Find(ArtworkKind.Snap, "clone", "Clone", parent: "parent"));
        Assert.NotNull(locator.Find(ArtworkKind.Title, "robby", "Robby Roto: The \"Game\""));
        Assert.Null(locator.Find(ArtworkKind.Marquee, "robby", "Robby"));
    }
}
