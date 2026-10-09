using Arcade.App;
using Arcade.Libretro;
using Arcade.Library;
using Microsoft.Data.Sqlite;

namespace Arcade.Tests;

public class RewindBufferTests
{
    /// <summary>Game-like states: mostly unchanged from one frame to the next.</summary>
    static List<byte[]> States(int count, int size, int seed, int maxChanges = 40)
    {
        var random = new Random(seed);
        var state = new byte[size];
        random.NextBytes(state);
        var list = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            for (var c = random.Next(maxChanges + 1); c > 0; c--)
                state[random.Next(size)] = (byte)random.Next(256);
            list.Add(state.ToArray());
        }
        return list;
    }

    [Fact]
    public void Steps_back_through_every_recorded_state()
    {
        var states = States(200, 3000, seed: 1);
        var buffer = new RewindBuffer(1 << 20, 1000);
        foreach (var state in states)
            buffer.Push(state);

        Assert.Equal(199, buffer.Count);
        Assert.Equal(states[^1], buffer.Peek().ToArray());
        for (var i = states.Count - 2; i >= 0; i--)
        {
            Assert.True(buffer.TryStepBack(out var state));
            Assert.Equal(states[i], state.ToArray());
        }
        Assert.False(buffer.TryStepBack(out _));
        Assert.Equal(states[0], buffer.Peek().ToArray());
    }

    [Fact]
    public void Keeps_at_most_the_step_limit()
    {
        var states = States(100, 500, seed: 2);
        var buffer = new RewindBuffer(1 << 20, 10);
        foreach (var state in states)
            buffer.Push(state);
        Assert.Equal(10, buffer.Count);
        for (var i = 98; i >= 89; i--)
        {
            Assert.True(buffer.TryStepBack(out var state));
            Assert.Equal(states[i], state.ToArray());
        }
        Assert.False(buffer.TryStepBack(out _));
    }

    [Fact]
    public void A_full_arena_drops_the_oldest_steps_and_never_corrupts_newer_ones()
    {
        // A tiny arena forces constant wrapping and eviction while play and rewinding interleave.
        var random = new Random(3);
        var states = States(3000, 700, seed: 4, maxChanges: 300);
        var buffer = new RewindBuffer(6000, 10_000);
        var model = new List<byte[]>(); // what the buffer should hold, oldest first
        var next = 0;
        var steppedBack = 0;
        while (next < states.Count)
        {
            if (random.Next(4) == 0)
            {
                for (var n = random.Next(1, 8); n > 0; n--)
                {
                    var ok = buffer.TryStepBack(out var state);
                    Assert.Equal(model.Count > 1, ok);
                    if (!ok)
                        break;
                    model.RemoveAt(model.Count - 1);
                    Assert.Equal(model[^1], state.ToArray());
                    steppedBack++;
                }
            }
            else
            {
                buffer.Push(states[next]);
                model.Add(states[next++]);
            }
            Assert.InRange(buffer.Count, 0, model.Count - 1);
            model.RemoveRange(0, model.Count - (buffer.Count + 1)); // the oldest were dropped
            Assert.Equal(model[^1], buffer.Peek().ToArray());
        }
        Assert.True(steppedBack > 100);
        Assert.InRange(buffer.BytesUsed, 1, buffer.Capacity);
    }

    [Fact]
    public void A_different_state_size_starts_over()
    {
        var buffer = new RewindBuffer(1 << 16, 100);
        buffer.Push(new byte[100]);
        buffer.Push(new byte[100]);
        Assert.Equal(1, buffer.Count);
        buffer.Push(new byte[120]);
        Assert.Equal(0, buffer.Count);
        Assert.Equal(120, buffer.Peek().Length);
    }
}

public class StateSlotsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("arcade-states").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Slots_save_load_and_delete_with_a_picture()
    {
        var slots = new StateSlots(_dir, "gridlee");
        Assert.All(slots.All(), s => Assert.True(s.IsEmpty));
        Assert.Null(slots.Newest);

        var picture = new Rgba32Image(4, 3, new byte[4 * 3 * 4]);
        slots.Save(3, [1, 2, 3], picture);
        slots.Save(5, [4, 5], null);
        File.SetLastWriteTime(Path.Combine(slots.Folder, "slot3.state"), DateTime.Now.AddMinutes(-5));

        Assert.Equal([3, 5], slots.All().Where(s => !s.IsEmpty).Select(s => s.Number));
        Assert.Equal(5, slots.Newest);
        Assert.Equal([1, 2, 3], slots.Load(3));
        Assert.True(File.Exists(slots.Get(3).ThumbnailPath));
        Assert.Null(slots.Load(4));

        slots.Delete(3);
        Assert.True(slots.Get(3).IsEmpty);
        Assert.False(File.Exists(slots.Get(3).ThumbnailPath));
    }

    [Fact]
    public void A_state_from_before_slots_shows_as_slot_one_until_slot_one_is_saved()
    {
        var legacy = Path.Combine(_dir, "gridlee.state");
        File.WriteAllBytes(legacy, [9, 9]);
        var slots = new StateSlots(Path.Combine(_dir, "states"), "gridlee", legacy);
        Assert.False(slots.Get(1).IsEmpty);
        Assert.Equal([9, 9], slots.Load(1));

        slots.Save(1, [7], null);
        Assert.False(File.Exists(legacy));
        Assert.Equal([7], slots.Load(1));
    }
}

public class PlayTimeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("arcade-playtime").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Upgrading_a_library_keeps_games_and_counts_and_adds_play_time()
    {
        var path = Path.Combine(_dir, "library.db");
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            // The schema as the first version of the library wrote it.
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE folders (path TEXT PRIMARY KEY);
                CREATE TABLE zips (path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime INTEGER NOT NULL, crcs BLOB NOT NULL);
                CREATE TABLE games (
                    set_name TEXT PRIMARY KEY COLLATE NOCASE, path TEXT NOT NULL, title TEXT NOT NULL, year TEXT, manufacturer TEXT,
                    parent TEXT, orientation INTEGER NOT NULL, players INTEGER, control TEXT, genre TEXT,
                    status INTEGER NOT NULL, core TEXT, problem TEXT, warnings TEXT);
                CREATE TABLE user_games (
                    set_name TEXT PRIMARY KEY COLLATE NOCASE, core_override TEXT, favorite INTEGER NOT NULL DEFAULT 0,
                    play_count INTEGER NOT NULL DEFAULT 0, last_played TEXT);
                INSERT INTO games VALUES ('gridlee', 'roms/gridlee.zip', 'Gridlee', '1983', 'Videa', NULL, 0, 2, NULL, NULL, 0, 'fbneo', NULL, NULL);
                INSERT INTO user_games VALUES ('gridlee', NULL, 1, 4, '2026-10-01T10:00:00.0000000Z');
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        using var library = new GameLibrary(path);
        var game = library.Find("gridlee");
        Assert.NotNull(game);
        Assert.Equal(4, game.PlayCount);
        Assert.True(game.Favorite);
        Assert.Equal(TimeSpan.Zero, game.PlayTime);

        library.AddPlayTime("gridlee", TimeSpan.FromMinutes(12.4));
        library.AddPlayTime("gridlee", TimeSpan.FromSeconds(36));
        Assert.Equal(TimeSpan.FromSeconds(780), library.Find("gridlee")!.PlayTime);
    }
}
