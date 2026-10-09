using System.IO.Compression;
using System.Net;

namespace Arcade.App;

/// <summary>
/// Installs or updates the emulator cores from the libretro buildbot (nightly builds), together
/// with each core's list of games (its DAT, which must match the core's version) and FBNeo's
/// hiscore.dat. A file is only replaced when the download differs. A core in use can't be
/// overwritten on Windows but can be renamed, so the old one is moved aside as <c>.old</c> and
/// deleted on a later start (<see cref="CleanUp"/>).
/// </summary>
sealed class CoreUpdater(AppPaths paths, HttpMessageHandler? handler = null)
{
    public const string BuildbotUrl = "https://buildbot.libretro.com/nightly/windows/x86_64/latest/";

    static readonly Dictionary<string, string> DatUrls = new()
    {
        ["fbneo.dat"] = "https://raw.githubusercontent.com/libretro/FBNeo/master/dats/FinalBurn%20Neo%20(ClrMame%20Pro%20XML%2C%20Arcade%20only).dat",
        ["mame2003_plus.dat"] = "https://raw.githubusercontent.com/libretro/mame2003-plus-libretro/master/metadata/mame2003-plus.xml",
    };

    const string FBNeoHiscoreUrl = "https://raw.githubusercontent.com/libretro/FBNeo/master/metadata/hiscore.dat";

    /// <param name="Updated">Files that were installed or changed, e.g. "fbneo_libretro.dll".</param>
    /// <param name="ListsChanged">A core's game list changed, so the library should be scanned again.</param>
    public sealed record Result(IReadOnlyList<string> Updated, IReadOnlyList<string> Problems, bool ListsChanged)
    {
        public bool CoresChanged => Updated.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds the download list; tests swap the addresses.</summary>
    internal IReadOnlyList<(string Url, string Path, string? ZipEntry)> Downloads(string buildbotUrl = BuildbotUrl) =>
    [
        .. CoreCatalog.Known.Select(c => (buildbotUrl + c.DllName + ".zip", Path.Combine(paths.Cores, c.DllName), (string?)c.DllName)),
        .. CoreCatalog.Known.Where(c => DatUrls.ContainsKey(c.DatName)).Select(c => (DatUrls[c.DatName], Path.Combine(paths.Dats, c.DatName), (string?)null)),
        (FBNeoHiscoreUrl, Path.Combine(paths.System, "fbneo", "hiscore.dat"), null),
    ];

    public Task<Result> UpdateAsync(Action<string>? report = null, CancellationToken cancel = default) =>
        UpdateAsync(Downloads(), report, cancel);

    internal async Task<Result> UpdateAsync(IReadOnlyList<(string Url, string Path, string? ZipEntry)> downloads, Action<string>? report, CancellationToken cancel)
    {
        using var http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5); // the MAME 2003-Plus core is a 20 MB download
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ArcadeEmulator-updater");
        var updated = new List<string>();
        var problems = new List<string>();
        var listsChanged = false;

        foreach (var (url, path, entry) in downloads)
        {
            var name = Path.GetFileName(path);
            report?.Invoke($"checking {name}…");
            byte[] data;
            try
            {
                using var response = await http.GetAsync(url, cancel);
                if (!response.IsSuccessStatusCode)
                {
                    problems.Add($"{name}: the server said {(int)response.StatusCode} {response.ReasonPhrase}");
                    continue;
                }
                data = await response.Content.ReadAsByteArrayAsync(cancel);
                if (entry != null)
                    data = Unzip(data, entry);
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or TaskCanceledException && !cancel.IsCancellationRequested)
            {
                problems.Add($"{name}: {(e is TaskCanceledException ? "the download timed out" : e.Message)}");
                continue;
            }

            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
            {
                report?.Invoke($"  {name} is up to date");
                continue;
            }
            try
            {
                Replace(path, data);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{name}: couldn't replace it ({e.Message})");
                continue;
            }
            updated.Add(name);
            listsChanged |= path.StartsWith(paths.Dats, StringComparison.OrdinalIgnoreCase);
            report?.Invoke($"  {name} {(data.Length > 1 << 20 ? $"({data.Length >> 20} MB) " : "")}updated");
        }
        return new Result(updated, problems, listsChanged);
    }

    static byte[] Unzip(byte[] zip, string entryName)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"the download doesn't contain {entryName}");
        using var stream = entry.Open();
        var data = new MemoryStream();
        stream.CopyTo(data);
        return data.ToArray();
    }

    /// <summary>Writes the new file beside the old one, moves the old one aside (allowed even while it's loaded), then puts the new one in place.</summary>
    static void Replace(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fresh = path + ".new";
        File.WriteAllBytes(fresh, data);
        if (File.Exists(path))
        {
            var old = path + ".old";
            if (File.Exists(old))
                File.Delete(old); // only possible once nothing has it loaded; fails (and reports) otherwise
            File.Move(path, old);
            File.Move(fresh, path);
            try { File.Delete(old); } // works unless the app has the old core loaded; then CleanUp gets it later
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return;
        }
        File.Move(fresh, path);
    }

    /// <summary>Deletes cores replaced in an earlier session, now that nothing has them loaded.</summary>
    public static void CleanUp(AppPaths paths)
    {
        if (!Directory.Exists(paths.Cores))
            return;
        foreach (var old in Directory.EnumerateFiles(paths.Cores, "*.old"))
        {
            try { File.Delete(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
