using System.Collections.Concurrent;
using System.Net;

namespace Arcade.App.Video;

/// <summary>
/// Fetches bezel artwork for the games in the library from The Bezel Project
/// (github.com/thebezelproject/bezelproject-MAME), whose arcade bezels are PNGs named by MAME set
/// with a see-through screen. Each goes to <c>artwork/bezels/&lt;set&gt;.png</c>, where
/// <see cref="Bezel.Find"/> looks; a clone without its own uses its parent's. Pictures are only
/// kept if a window for the game can be found in them.
/// </summary>
sealed class BezelDownloader(string artworkRoot, HttpMessageHandler? handler = null, string baseUrl = BezelDownloader.BezelProjectUrl)
{
    public const string BezelProjectUrl = "https://raw.githubusercontent.com/thebezelproject/bezelproject-MAME/master/retroarch/overlay/";

    /// <summary>The project's general bezels, used for games without their own.</summary>
    static readonly (string Remote, string Local)[] Defaults =
    [
        ("MAME-Horizontal.png", "default-horizontal"),
        ("MAME-Vertical.png", "default-vertical"),
    ];

    /// <param name="Downloaded">Pictures saved (a parent's shared by its clones counts once).</param>
    /// <param name="AlreadyHad">Games that already had a bezel of their own or their parent's.</param>
    /// <param name="NotAvailable">Games The Bezel Project has no bezel for (they use the general one).</param>
    public sealed record Result(int Downloaded, int AlreadyHad, int NotAvailable, IReadOnlyList<string> Problems);

    string Folder => Path.Combine(artworkRoot, "bezels");

    /// <param name="games">Set names with their parent set, if they're clones.</param>
    public async Task<Result> DownloadAsync(IEnumerable<(string Set, string? Parent)> games, Action<string>? report = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(Folder);
        using var http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ArcadeEmulator-bezels");

        int defaultsSaved = 0, had = 0, missing = 0;
        var problems = new ConcurrentQueue<string>();

        foreach (var (remote, local) in Defaults)
        {
            if (Has(local))
                continue;
            switch (await TryFetch(http, remote, local, cancel))
            {
                case Fetch.Saved: defaultsSaved++; report?.Invoke($"got   {local} (for games without their own)"); break;
                case Fetch.Bad(var why): problems.Enqueue($"{local}: {why}"); break;
            }
        }

        // A parent and its clones share one bezel: ask for each name once.
        var wanted = games.DistinctBy(g => g.Set, StringComparer.OrdinalIgnoreCase).ToList();
        // (Lazy, as GetOrAdd may run its factory twice when two clones ask for their parent together.)
        var asked = new ConcurrentDictionary<string, Lazy<Task<Fetch>>>(StringComparer.OrdinalIgnoreCase);
        Task<Fetch> Get(string name) => asked.GetOrAdd(name, n => new(() => TryFetch(http, "ArcadeBezels/" + n + ".png", n, cancel))).Value;

        await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancel }, async (game, ct) =>
        {
            if (Has(game.Set) || (game.Parent != null && Has(game.Parent)))
            {
                Interlocked.Increment(ref had);
                return;
            }
            var result = await Get(game.Set);
            var from = game.Set;
            if (result is Fetch.Missing && game.Parent != null)
            {
                result = await Get(game.Parent);
                from = game.Parent;
            }
            switch (result)
            {
                case Fetch.Saved:
                    report?.Invoke(from == game.Set ? $"got   {game.Set}" : $"got   {game.Set} (its parent {from}'s)");
                    break;
                case Fetch.Missing:
                    Interlocked.Increment(ref missing);
                    break;
                case Fetch.Bad(var why):
                    problems.Enqueue($"{game.Set}: {why}");
                    break;
            }
        });
        var downloaded = defaultsSaved + asked.Values.Count(t => t.Value.Result is Fetch.Saved);
        return new Result(downloaded, had, missing, problems.ToList());
    }

    bool Has(string name) => File.Exists(Path.Combine(Folder, name + ".png"));

    abstract record Fetch
    {
        public sealed record Saved : Fetch;
        public sealed record Missing : Fetch;
        public sealed record Bad(string Why) : Fetch;
    }

    async Task<Fetch> TryFetch(HttpClient http, string remote, string local, CancellationToken cancel)
    {
        byte[] data;
        try
        {
            using var response = await http.GetAsync(baseUrl + string.Join('/', remote.Split('/').Select(Uri.EscapeDataString)), cancel);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new Fetch.Missing();
            if (!response.IsSuccessStatusCode)
                return new Fetch.Bad($"the server said {(int)response.StatusCode} {response.ReasonPhrase}");
            data = await response.Content.ReadAsByteArrayAsync(cancel);
        }
        catch (HttpRequestException e)
        {
            return new Fetch.Bad("couldn't download: " + e.Message);
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new Fetch.Bad("the download timed out");
        }

        // Keep it only if it works as a bezel; write beside it first so a half-written file never shows.
        var path = Path.Combine(Folder, local + ".png");
        var temp = path + ".download";
        await File.WriteAllBytesAsync(temp, data, cancel);
        try
        {
            Bezel.Load(temp);
        }
        catch (InvalidDataException e)
        {
            File.Delete(temp);
            return new Fetch.Bad(e.Message.Replace(Path.GetFileName(temp), local + ".png"));
        }
        File.Move(temp, path, overwrite: true);
        return new Fetch.Saved();
    }
}
