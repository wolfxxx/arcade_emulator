using Microsoft.Data.Sqlite;

namespace Arcade.Library;

/// <summary>A game as stored in the library, including the user's own settings for it.</summary>
public sealed record LibraryGame(
    string SetName,
    string Path,
    string Title,
    string? Year,
    string? Manufacturer,
    string? Parent,
    Orientation Orientation,
    int? Players,
    string? Control,
    string? Genre,
    GameStatus Status,
    string? CoreId,
    string? Problem,
    IReadOnlyList<string> Warnings,
    string? CoreOverride,
    bool Favorite,
    int PlayCount,
    DateTime? LastPlayed);

public sealed record LibraryQuery(string? Search = null, bool IncludeUnplayable = false, bool FavoritesOnly = false, bool IncludeClones = true);

/// <summary>
/// The SQLite game library: ROM folders, a cache of zip CRCs (so rescans skip unchanged files),
/// the scanned games, and per-game user data (core override, favourite, play count) that survives rescans.
/// </summary>
public sealed class GameLibrary : IDisposable
{
    const int SchemaVersion = 1;
    readonly SqliteConnection _db;
    SqliteTransaction? _tx;

    public GameLibrary(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode = WAL");
        Migrate();
    }

    public void Dispose() => _db.Dispose();

    void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version == SchemaVersion)
            return;
        if (version != 0)
        {
            // Scanned data can always be rebuilt; keep the user's data.
            Execute("DROP TABLE IF EXISTS games; DROP TABLE IF EXISTS zips;");
        }
        Execute($"""
            CREATE TABLE IF NOT EXISTS folders (path TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS zips (path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime INTEGER NOT NULL, crcs BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS games (
                set_name TEXT PRIMARY KEY COLLATE NOCASE, path TEXT NOT NULL, title TEXT NOT NULL, year TEXT, manufacturer TEXT,
                parent TEXT, orientation INTEGER NOT NULL, players INTEGER, control TEXT, genre TEXT,
                status INTEGER NOT NULL, core TEXT, problem TEXT, warnings TEXT);
            CREATE TABLE IF NOT EXISTS user_games (
                set_name TEXT PRIMARY KEY COLLATE NOCASE, core_override TEXT, favorite INTEGER NOT NULL DEFAULT 0,
                play_count INTEGER NOT NULL DEFAULT 0, last_played TEXT);
            PRAGMA user_version = {SchemaVersion};
            """);
    }

    // ---- Folders ----

    public IReadOnlyList<string> Folders => Query("SELECT path FROM folders ORDER BY path", r => r.GetString(0));

    public void AddFolder(string path) => Execute("INSERT OR IGNORE INTO folders VALUES ($p)", ("$p", System.IO.Path.GetFullPath(path)));

    public bool RemoveFolder(string path) => Execute("DELETE FROM folders WHERE path = $p", ("$p", System.IO.Path.GetFullPath(path))) > 0;

    // ---- Zip CRC cache ----

    internal Dictionary<string, (long Size, long MTime, uint[] Crcs)> LoadZipCache()
    {
        var cache = new Dictionary<string, (long, long, uint[])>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, size, mtime, blob) in Query("SELECT path, size, mtime, crcs FROM zips", r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2), (byte[])r[3])))
            cache[path] = (size, mtime, ToCrcs(blob));
        return cache;
    }

    internal void SaveZipCache(IEnumerable<(string Path, long Size, long MTime, uint[] Crcs)> zips) => InTransaction(() =>
    {
        Execute("DELETE FROM zips");
        using var insert = Command("INSERT INTO zips VALUES ($p, $s, $m, $c)");
        foreach (var (path, size, mtime, crcs) in zips)
        {
            Bind(insert, ("$p", path), ("$s", size), ("$m", mtime), ("$c", FromCrcs(crcs)));
            insert.ExecuteNonQuery();
        }
    });

    static byte[] FromCrcs(uint[] crcs)
    {
        var bytes = new byte[crcs.Length * 4];
        Buffer.BlockCopy(crcs, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    static uint[] ToCrcs(byte[] bytes)
    {
        var crcs = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, crcs, 0, crcs.Length * 4);
        return crcs;
    }

    // ---- Games ----

    internal void ReplaceGames(IEnumerable<LibraryGame> games) => InTransaction(() =>
    {
        Execute("DELETE FROM games");
        using var insert = Command("INSERT INTO games VALUES ($set, $path, $title, $year, $mf, $parent, $orient, $players, $control, $genre, $status, $core, $problem, $warn)");
        foreach (var g in games)
        {
            Bind(insert,
                ("$set", g.SetName), ("$path", g.Path), ("$title", g.Title), ("$year", g.Year), ("$mf", g.Manufacturer),
                ("$parent", g.Parent), ("$orient", (int)g.Orientation), ("$players", g.Players), ("$control", g.Control),
                ("$genre", g.Genre), ("$status", (int)g.Status), ("$core", g.CoreId), ("$problem", g.Problem),
                ("$warn", g.Warnings.Count == 0 ? null : string.Join('\n', g.Warnings)));
            insert.ExecuteNonQuery();
        }
    });

    const string SelectGames = """
        SELECT g.set_name, g.path, g.title, g.year, g.manufacturer, g.parent, g.orientation, g.players, g.control, g.genre,
               g.status, g.core, g.problem, g.warnings, u.core_override, COALESCE(u.favorite, 0), COALESCE(u.play_count, 0), u.last_played
        FROM games g LEFT JOIN user_games u ON u.set_name = g.set_name
        """;

    public LibraryGame? Find(string setName) =>
        Query(SelectGames + " WHERE g.set_name = $s", ReadGame, ("$s", setName)).FirstOrDefault();

    public IReadOnlyList<LibraryGame> Games(LibraryQuery? query = null)
    {
        query ??= new LibraryQuery();
        var where = new List<string>();
        if (!query.IncludeUnplayable)
            where.Add($"g.status = {(int)GameStatus.Playable}");
        if (query.FavoritesOnly)
            where.Add("u.favorite = 1");
        if (!query.IncludeClones)
            where.Add("g.parent IS NULL");
        if (!string.IsNullOrWhiteSpace(query.Search))
            where.Add("(g.title LIKE $q OR g.set_name LIKE $q OR g.manufacturer LIKE $q OR g.genre LIKE $q)");
        var sql = SelectGames + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY g.title COLLATE NOCASE";
        return Query(sql, ReadGame, ("$q", $"%{query.Search?.Trim()}%"));
    }

    public int Count(GameStatus status) => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM games WHERE status = $s", ("$s", (int)status)));

    static LibraryGame ReadGame(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3), Str(r, 4), Str(r, 5), (Orientation)r.GetInt32(6),
        r.IsDBNull(7) ? null : r.GetInt32(7), Str(r, 8), Str(r, 9), (GameStatus)r.GetInt32(10), Str(r, 11), Str(r, 12),
        Str(r, 13)?.Split('\n') ?? [], Str(r, 14), r.GetInt64(15) != 0, r.GetInt32(16),
        Str(r, 17) is { } played ? DateTime.Parse(played, null, System.Globalization.DateTimeStyles.RoundtripKind) : null);

    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ---- User data ----

    /// <summary>Overrides the automatic core choice for a set; null restores automatic choice. Takes effect on the next scan.</summary>
    public void SetCoreOverride(string setName, string? coreId) => UpsertUser(setName, "core_override", coreId);

    public void SetFavorite(string setName, bool favorite) => UpsertUser(setName, "favorite", favorite ? 1 : 0);

    public void RecordPlay(string setName, DateTime when) =>
        Execute("""
            INSERT INTO user_games (set_name, play_count, last_played) VALUES ($s, 1, $t)
            ON CONFLICT(set_name) DO UPDATE SET play_count = play_count + 1, last_played = $t
            """, ("$s", setName), ("$t", when.ToUniversalTime().ToString("O")));

    public IReadOnlyDictionary<string, string> CoreOverrides() =>
        Query("SELECT set_name, core_override FROM user_games WHERE core_override IS NOT NULL", r => (r.GetString(0), r.GetString(1)))
            .ToDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase);

    void UpsertUser(string setName, string column, object? value) =>
        Execute($"INSERT INTO user_games (set_name, {column}) VALUES ($s, $v) ON CONFLICT(set_name) DO UPDATE SET {column} = $v",
            ("$s", setName), ("$v", value));

    // ---- Helpers ----

    void InTransaction(Action body)
    {
        _tx = _db.BeginTransaction();
        try
        {
            body();
            _tx.Commit();
        }
        finally
        {
            _tx.Dispose();
            _tx = null;
        }
    }

    SqliteCommand Command(string sql)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        return cmd;
    }

    static void Bind(SqliteCommand cmd, params (string Name, object? Value)[] args)
    {
        cmd.Parameters.Clear();
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    int Execute(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql);
        Bind(cmd, args);
        return cmd.ExecuteNonQuery();
    }

    object? Scalar(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql);
        Bind(cmd, args);
        return cmd.ExecuteScalar();
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string, object?)[] args)
    {
        using var cmd = Command(sql);
        Bind(cmd, args);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
            list.Add(read(reader));
        return list;
    }
}
