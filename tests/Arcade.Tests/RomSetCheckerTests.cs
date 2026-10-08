using System.IO.Compression;
using System.Text;
using Arcade.Library;

namespace Arcade.Tests;

public class RomSetCheckerTests : IDisposable
{
    // Rather than hard-coding CRCs, each test DAT is built from the CRCs of the content it zips.
    readonly string _dir = Directory.CreateTempSubdirectory("arcade-romcheck").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string MakeZip(string name, params (string File, string Content)[] entries)
    {
        var path = Path.Combine(_dir, name + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (file, content) in entries)
        {
            using var s = zip.CreateEntry(file).Open();
            s.Write(Encoding.ASCII.GetBytes(content));
        }
        return path;
    }

    static uint Crc(string content)
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            using (var s = zip.CreateEntry("x").Open())
                s.Write(Encoding.ASCII.GetBytes(content));
            using var read = ZipFile.OpenRead(path);
            return read.Entries[0].Crc32;
        }
        finally { File.Delete(path); }
    }

    static DatFile Dat(string xml) => DatFile.Load(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void Parses_games_descriptions_and_roms_including_the_rom_right_after_description()
    {
        var dat = Dat("""
            <?xml version="1.0"?>
            <!DOCTYPE mame [ <!ELEMENT mame (game+)> ]>
            <mame>
              <game name="parent"><description>Parent Game</description><rom name="a.bin" size="4" crc="0000abcd"/><rom name="b.bin" size="4" status="nodump"/></game>
              <machine name="clone" cloneof="parent" romof="parent"><description>Clone</description><rom name="c.bin" size="4" crc="1234"/></machine>
            </mame>
            """);
        var parent = dat.Find("PARENT")!; // lookups ignore case, like Windows file names
        Assert.Equal("Parent Game", parent.Description);
        Assert.Equal(2, parent.Roms.Count);
        Assert.Equal(0xabcdu, parent.Roms[0].Crc);
        Assert.True(parent.Roms[1].NoDump);
        Assert.Equal("parent", dat.Find("clone")!.RomOf);
    }

    [Fact]
    public void Complete_when_every_crc_is_in_the_zip_regardless_of_file_names()
    {
        var rom = MakeZip("game", ("renamed1.bin", "AAAA"), ("renamed2.bin", "BBBB"));
        var dat = Dat($"""<datafile><game name="game"><rom name="a" crc="{Crc("AAAA"):x8}"/><rom name="b" crc="{Crc("BBBB"):x8}"/></game></datafile>""");
        Assert.Equal(RomSetStatus.Complete, RomSetChecker.Check(dat, rom, []).Status);
    }

    [Fact]
    public void Reports_missing_files_by_name()
    {
        var rom = MakeZip("game", ("a", "AAAA"));
        var dat = Dat($"""<datafile><game name="game"><rom name="a" crc="{Crc("AAAA"):x8}"/><rom name="sound.bin" crc="{Crc("SND"):x8}"/></game></datafile>""");
        var check = RomSetChecker.Check(dat, rom, []);
        Assert.Equal(RomSetStatus.Incomplete, check.Status);
        Assert.Equal("sound.bin", Assert.Single(check.Missing).Name);
    }

    [Fact]
    public void Finds_parent_and_bios_roms_in_their_own_zips()
    {
        var systemDir = Directory.CreateDirectory(Path.Combine(_dir, "system")).FullName;
        MakeZip("parent", ("p", "PARENT"));
        File.Move(MakeZip("bios", ("b", "BIOS")), Path.Combine(systemDir, "bios.zip"));
        var clone = MakeZip("clone", ("c", "CLONE"));
        var dat = Dat($"""
            <datafile>
              <game name="bios" isbios="yes"><rom name="b" crc="{Crc("BIOS"):x8}"/></game>
              <game name="parent" romof="bios"><rom name="p" crc="{Crc("PARENT"):x8}"/><rom name="b" crc="{Crc("BIOS"):x8}"/></game>
              <game name="clone" cloneof="parent" romof="parent"><rom name="c" crc="{Crc("CLONE"):x8}"/><rom name="p" crc="{Crc("PARENT"):x8}"/><rom name="b" crc="{Crc("BIOS"):x8}"/></game>
            </datafile>
            """);

        Assert.Equal(RomSetStatus.Complete, RomSetChecker.Check(dat, clone, [systemDir]).Status);
        Assert.Equal(RomSetStatus.Incomplete, RomSetChecker.Check(dat, clone, []).Status); // BIOS not found
    }

    [Fact]
    public void Unknown_set_name()
    {
        var rom = MakeZip("mystery", ("a", "AAAA"));
        Assert.Equal(RomSetStatus.UnknownSet, RomSetChecker.Check(Dat("<datafile/>"), rom, []).Status);
    }

    [Fact]
    public void Real_dats_route_the_free_roms_to_the_right_core()
    {
        var fbneoDat = Path.Combine(TestPaths.RepoRoot, "dats", "fbneo.dat");
        var mameDat = Path.Combine(TestPaths.RepoRoot, "dats", "mame2003_plus.dat");
        if (!File.Exists(fbneoDat) || !File.Exists(mameDat) || !File.Exists(TestPaths.Rom("alienar")))
            return; // needs tools/fetch-deps.ps1

        var fbneo = DatFile.Load(fbneoDat);
        var mame = DatFile.Load(mameDat);

        Assert.Equal(RomSetStatus.Complete, RomSetChecker.Check(fbneo, TestPaths.Rom("robby"), []).Status);
        Assert.Equal(RomSetStatus.UnknownSet, RomSetChecker.Check(fbneo, TestPaths.Rom("supertnk"), []).Status);
        var alienar = RomSetChecker.Check(fbneo, TestPaths.Rom("alienar"), []);
        Assert.Equal(RomSetStatus.Incomplete, alienar.Status);
        Assert.Equal(["sg.snd", "decoder.4", "decoder.6"], alienar.Missing.Select(r => r.Name));
        foreach (var rom in new[] { "alienar", "supertnk", "robby", "gridlee" })
            Assert.Equal(RomSetStatus.Complete, RomSetChecker.Check(mame, TestPaths.Rom(rom), []).Status);
    }
}
