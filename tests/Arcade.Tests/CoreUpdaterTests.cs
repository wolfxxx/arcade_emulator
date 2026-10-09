using System.IO.Compression;
using System.Net;
using Arcade.App;

namespace Arcade.Tests;

public class CoreUpdaterTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "arcade-update-" + Guid.NewGuid().ToString("N"));
    AppPaths Paths => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    static byte[] Zip(string entry, byte[] content)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var e = zip.CreateEntry(entry).Open())
            e.Write(content);
        return stream.ToArray();
    }

    /// <summary>Serves whatever is in <see cref="Files"/> by full URL.</summary>
    sealed class FakeServer : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(Files.TryGetValue(request.RequestUri!.OriginalString, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task Installs_then_only_replaces_what_changed_keeping_the_old_core_aside()
    {
        var server = new FakeServer();
        var updater = new CoreUpdater(Paths, server);
        var downloads = updater.Downloads("https://bot.test/");
        void Serve(string version)
        {
            foreach (var (url, path, entry) in downloads)
                server.Files[url] = entry != null
                    ? Zip(entry, System.Text.Encoding.UTF8.GetBytes("core " + version + entry))
                    : System.Text.Encoding.UTF8.GetBytes("list " + Path.GetFileName(path));
        }

        // First run: everything is installed.
        Serve("1");
        var first = await updater.UpdateAsync(downloads, null, default);
        Assert.Empty(first.Problems);
        Assert.Equal(downloads.Count, first.Updated.Count);
        Assert.True(first.ListsChanged && first.CoresChanged);
        var fbneo = Path.Combine(Paths.Cores, "fbneo_libretro.dll");
        Assert.Equal("core 1fbneo_libretro.dll", File.ReadAllText(fbneo));
        Assert.True(File.Exists(Path.Combine(Paths.System, "fbneo", "hiscore.dat")));

        // Nothing new: nothing touched.
        var second = await updater.UpdateAsync(downloads, null, default);
        Assert.Empty(second.Updated);
        Assert.False(second.ListsChanged);

        // A new core build: it replaces the old one, which waits beside it to be deleted later.
        // A core in use can be renamed but not overwritten, so check with a real one loaded.
        var real = TestPaths.Core("fbneo");
        if (File.Exists(real))
            File.Copy(real, fbneo, overwrite: true);
        Serve("2");
        var loaded = File.Exists(real) ? System.Runtime.InteropServices.NativeLibrary.Load(fbneo) : 0;
        try
        {
            var third = await updater.UpdateAsync(downloads, null, default);
            Assert.Empty(third.Problems);
            Assert.Equal(["fbneo_libretro.dll", "mame2003_plus_libretro.dll"], third.Updated);
            Assert.False(third.ListsChanged);
            Assert.Equal("core 2fbneo_libretro.dll", File.ReadAllText(fbneo));
            Assert.Equal(loaded != 0, File.Exists(fbneo + ".old")); // kept only while loaded
            CoreUpdater.CleanUp(Paths); // still loaded: it stays for now
            Assert.Equal(loaded != 0, File.Exists(fbneo + ".old"));
        }
        finally
        {
            if (loaded != 0)
                System.Runtime.InteropServices.NativeLibrary.Free(loaded);
        }
        CoreUpdater.CleanUp(Paths); // next start: nothing has it loaded any more
        Assert.False(File.Exists(fbneo + ".old"));
        Assert.Empty(Directory.GetFiles(Paths.Cores, "*.new"));
    }

    [Fact]
    public async Task A_failed_download_leaves_the_installed_files_alone()
    {
        var server = new FakeServer();
        var updater = new CoreUpdater(Paths, server);
        var downloads = updater.Downloads("https://bot.test/");
        Directory.CreateDirectory(Paths.Cores);
        var fbneo = Path.Combine(Paths.Cores, "fbneo_libretro.dll");
        File.WriteAllText(fbneo, "installed");
        server.Files[downloads[0].Url] = [1, 2, 3]; // not a zip

        var result = await updater.UpdateAsync(downloads, null, default);

        Assert.Empty(result.Updated);
        Assert.Equal(downloads.Count, result.Problems.Count); // one broken, the rest missing on the server
        Assert.Equal("installed", File.ReadAllText(fbneo));
    }

    [RequiresCoreFact("mame2003_plus", "robby")]
    public void Robby_Roto_runs_on_MAME_2003_Plus_unless_the_player_chooses()
    {
        // FBNeo can play it too, but keeps no high scores for it.
        var catalog = new CoreCatalog(new AppPaths(TestPaths.RepoRoot));
        Assert.Equal("mame2003_plus", catalog.Choose(TestPaths.Rom("robby"), null).Core!.Id);
        Assert.Equal("fbneo", catalog.Choose(TestPaths.Rom("robby"), "fbneo").Core!.Id);
        Assert.Equal("fbneo", catalog.Choose(TestPaths.Rom("gridlee"), null).Core!.Id);
    }
}
