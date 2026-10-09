using System.Collections.Concurrent;
using System.Net;
using Arcade.App.Video;
using Arcade.Libretro;

namespace Arcade.Tests;

public class BezelDownloaderTests : IDisposable
{
    readonly string _artwork = Path.Combine(Path.GetTempPath(), "arcade-bezels-" + Guid.NewGuid().ToString("N"));
    string Bezels => Path.Combine(_artwork, "bezels");

    public void Dispose()
    {
        if (Directory.Exists(_artwork))
            Directory.Delete(_artwork, true);
    }

    /// <summary>A small bezel: opaque frame, see-through middle (or none at all).</summary>
    static byte[] Png(bool window = true)
    {
        const int w = 64, h = 48;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var inside = window && x >= 16 && x < 48 && y >= 8 && y < 40;
                pixels[(y * w + x) * 4 + 2] = 200;
                pixels[(y * w + x) * 4 + 3] = inside ? (byte)0 : (byte)255;
            }
        using var stream = new MemoryStream();
        PngEncoder.Write(new Rgba32Image(w, h, pixels), stream);
        return stream.ToArray();
    }

    sealed class FakeServer(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public readonly ConcurrentBag<string> Asked = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Asked.Add(path);
            return Task.FromResult(files.TryGetValue(path, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Fetches_each_games_bezel_falling_back_to_its_parents()
    {
        var server = new FakeServer(new()
        {
            ["MAME-Horizontal.png"] = Png(),
            ["MAME-Vertical.png"] = Png(),
            ["ArcadeBezels/gridlee.png"] = Png(),
            ["ArcadeBezels/sf2.png"] = Png(),
            ["ArcadeBezels/broken.png"] = Png(window: false),
        });
        Directory.CreateDirectory(Bezels);
        File.WriteAllBytes(Path.Combine(Bezels, "robby.png"), Png()); // already there: left alone

        var reports = new ConcurrentBag<string>();
        var result = await new BezelDownloader(_artwork, server, "https://bezels.test/")
            .DownloadAsync([("gridlee", null), ("sf2ua", "sf2"), ("sf2ub", "sf2"), ("robby", null), ("nothere", null), ("broken", null)], reports.Add);

        Assert.True(File.Exists(Path.Combine(Bezels, "gridlee.png")));
        Assert.True(File.Exists(Path.Combine(Bezels, "sf2.png")));      // two clones share their parent's
        Assert.False(File.Exists(Path.Combine(Bezels, "sf2ua.png")));
        Assert.True(File.Exists(Path.Combine(Bezels, "default-horizontal.png")));
        Assert.True(File.Exists(Path.Combine(Bezels, "default-vertical.png")));
        Assert.False(File.Exists(Path.Combine(Bezels, "broken.png"))); // no window: not kept
        Assert.Empty(Directory.GetFiles(Bezels, "*.download"));

        Assert.Equal(4, result.Downloaded); // two defaults, gridlee, and sf2 once for both clones
        Assert.Equal(1, result.AlreadyHad);
        Assert.Equal(1, result.NotAvailable);
        Assert.Contains(result.Problems, p => p.StartsWith("broken: ") && p.Contains("broken.png"));
        Assert.Equal(1, server.Asked.Count(a => a == "ArcadeBezels/sf2.png"));
        Assert.DoesNotContain("ArcadeBezels/robby.png", server.Asked);
        Assert.Contains(reports, r => r.Contains("sf2ua") && r.Contains("parent sf2"));

        // The game picks the downloaded pictures up.
        Assert.Equal(Path.Combine(Bezels, "sf2.png"), Bezel.Find(_artwork, "sf2ub", "sf2", vertical: false));
        Assert.Equal(Path.Combine(Bezels, "default-vertical.png"), Bezel.Find(_artwork, "nothere", null, vertical: true));

        // A second run has nothing left to fetch.
        var again = await new BezelDownloader(_artwork, server, "https://bezels.test/").DownloadAsync([("gridlee", null), ("sf2ua", "sf2")]);
        Assert.Equal(0, again.Downloaded);
        Assert.Equal(2, again.AlreadyHad);
    }
}
