using Arcade.Libretro;

namespace Arcade.Tests;

public class HeadlessCoreTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static CoreHost LoadCore(string core, string rom, IInputSource? input = null)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "arcade-tests");
        var host = CoreHost.Load(TestPaths.Core(core), new CoreHostOptions
        {
            SystemDirectory = Path.Combine(scratch, "system"),
            SaveDirectory = Path.Combine(scratch, "saves"),
            Input = input,
        });
        host.LoadGame(TestPaths.Rom(rom));
        return host;
    }

    static void Run(CoreHost host, int frames)
    {
        for (var i = 0; i < frames; i++)
            host.RunFrame();
    }

    [RequiresCoreFact("mame2003_plus", "robby")]
    public void Mame2003Plus_runs_robby_roto_with_video_and_audio()
    {
        using var host = LoadCore("mame2003_plus", "robby");
        Run(host, 600);

        Assert.Equal(320, host.LastFrame.Width);
        Assert.Equal(204, host.LastFrame.Height);
        Assert.False(host.LastFrame.ToRgba32().IsUniform(), "Expected a non-blank frame after 600 frames.");
        var expectedAudio = 600 / host.AvInfo.Timing.Fps * host.AvInfo.Timing.SampleRate;
        Assert.InRange(host.AudioFramesReceived, expectedAudio * 0.99, expectedAudio * 1.01);
        Assert.NotEmpty(host.Options);
    }

    [RequiresCoreFact("mame2003_plus", "supertnk")]
    public void Vertical_game_reports_rotation()
    {
        using var host = LoadCore("mame2003_plus", "supertnk");
        Run(host, 1);
        Assert.Equal(3u, host.Rotation); // 270° CCW
        var rotated = host.LastFrame.ToRgba32().RotateCcw(host.Rotation);
        Assert.Equal(host.LastFrame.Height, rotated.Width);
    }

    [RequiresCoreFact("fbneo", "gridlee")]
    public void Core_names_the_game_controls()
    {
        using var host = LoadCore("fbneo", "gridlee");
        Run(host, 1); // FBNeo describes its controls once the game is running
        // The controls screen shows these next to each button.
        var all = string.Join("; ", host.InputDescriptors.Select(d => $"{d.Port}/{d.Device}/{d.Index}/{d.Id}={d.Description}"));
        Assert.True(host.InputDescriptors.Any(d => d.Port == 0 && d.Device == RetroDevice.Joypad && d.Id == (uint)JoypadButton.B && d.Description == "Button 1"), all);
        Assert.True(host.InputDescriptors.Any(d => d.Port == 0 && d.Id == (uint)JoypadButton.Select), all);
    }

    [RequiresCoreFact("fbneo", "gridlee")]
    public void Save_state_round_trip_is_deterministic_with_input()
    {
        // The input script runs on its own clock so it can be rewound together with the save state.
        long clock = 0;
        var input = new ScriptedInput(() => clock)
            .Press(120, JoypadButton.Select)
            .Press(180, JoypadButton.Start)
            .Press(260, JoypadButton.Left, 40)
            .Press(320, JoypadButton.Right, 40);
        using var host = LoadCore("fbneo", "gridlee", input);

        void Step(int frames)
        {
            for (var i = 0; i < frames; i++, clock++)
                host.RunFrame();
        }

        Step(240);
        var state = host.SaveState();
        var savedClock = clock;
        Step(150);
        var expected = host.LastFrame.ComputeHash();

        host.LoadState(state);
        clock = savedClock;
        Step(150);

        Assert.Equal(expected, host.LastFrame.ComputeHash());
    }

    [RequiresCoreFact("fbneo", "gridlee")]
    public void Rewinding_returns_to_earlier_frames_exactly()
    {
        long clock = 0;
        var input = new ScriptedInput(() => clock)
            .Press(60, JoypadButton.Select)
            .Press(120, JoypadButton.Start)
            .Press(180, JoypadButton.Left, 60)
            .Press(260, JoypadButton.Right, 60);
        using var host = LoadCore("fbneo", "gridlee", input);
        var rewind = new RewindBuffer(16 << 20, 1000);
        var states = new List<byte[]>();
        var frames = new List<ulong>();
        var buffer = new byte[host.StateSize];
        for (; clock < 360; clock++)
        {
            host.RunFrame();
            Assert.True(host.TrySaveState(buffer));
            rewind.Push(buffer);
            states.Add(buffer.ToArray());
            frames.Add(host.LastFrame.ComputeHash());
        }

        // Each compressed step is a small fraction of a whole state.
        var perStep = rewind.BytesUsed / (double)rewind.Count;
        output.WriteLine($"State {buffer.Length} bytes, {perStep:F0} bytes per rewind step ({perStep * 3600 / 1e6:F1} MB per minute at 60 fps)");
        Assert.True(perStep < buffer.Length / 4.0, $"{perStep:F0} bytes per step for a {buffer.Length}-byte state");

        // Step back 150 frames (as the game scene does: restore, then run one frame to show it).
        for (var i = 0; i < 150; i++)
        {
            Assert.True(rewind.TryStepBack(out var state));
            Assert.Equal(states[^(i + 2)], state.ToArray());
            host.LoadState(state);
            clock = states.Count - (i + 2) + 1;
            host.RunFrame();
            Assert.Equal(frames[^(i + 1)], host.LastFrame.ComputeHash());
        }

        // Playing on from there with the same input gives the same game as before.
        for (clock++; clock < 360; clock++)
            host.RunFrame();
        Assert.Equal(frames[^1], host.LastFrame.ComputeHash());
    }

    [RequiresCoreFact("fbneo", "gridlee")]
    public void FBNeo_lists_a_games_cheats_as_options()
    {
        var cheats = Path.Combine(Path.GetTempPath(), "arcade-tests", "system", "fbneo", "cheats");
        Directory.CreateDirectory(cheats);
        var file = Path.Combine(cheats, "gridlee.ini");
        File.WriteAllText(file, """
            cheat "Test Poke"
            default 0
            0 "Disabled"
            1 "Enabled", 0, 0x0010, 0x00
            """);
        try
        {
            using var host = LoadCore("fbneo", "gridlee");
            var cheat = Assert.Single(host.Options.Values, o => o.Key.StartsWith("fbneo-cheat-"));
            Assert.Contains("Test Poke", cheat.Description);
            Assert.Equal(["0 - Disabled", "1 - Enabled"], cheat.Values);
            host.SetOption(cheat.Key, "1 - Enabled");
            Run(host, 60);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [RequiresCoreFact("fbneo", "gridlee")]
    public void Core_can_be_unloaded_and_loaded_again_in_one_process()
    {
        ulong first, second;
        using (var host = LoadCore("fbneo", "gridlee"))
        {
            Run(host, 300);
            first = host.LastFrame.ComputeHash();
        }
        using (var host = LoadCore("fbneo", "gridlee"))
        {
            Run(host, 300);
            second = host.LastFrame.ComputeHash();
        }
        Assert.Equal(first, second);
    }

    [RequiresCoreFact("mame2003_plus", "supertnk")]
    public void Switching_cores_and_games_repeatedly_gives_the_same_frames()
    {
        // The game browser runs one game after another in a single process, so each core must
        // come back clean after being unloaded, even with the other core loaded in between.
        (string Core, string Rom)[] sequence =
            [("mame2003_plus", "robby"), ("fbneo", "gridlee"), ("mame2003_plus", "supertnk"), ("mame2003_plus", "robby"), ("fbneo", "gridlee"), ("mame2003_plus", "supertnk")];
        var hashes = new List<ulong>();
        foreach (var (core, rom) in sequence)
        {
            using var host = LoadCore(core, rom);
            Run(host, 200);
            hashes.Add(host.LastFrame.ComputeHash());
        }
        Assert.Equal(hashes[..3], hashes[3..]);
    }

    [RequiresCoreFact("mame2003_plus", "robby")]
    public void Loading_a_second_core_while_one_is_active_is_rejected()
    {
        using var host = LoadCore("mame2003_plus", "robby");
        Assert.Throws<InvalidOperationException>(() => LoadCore("mame2003_plus", "robby"));
    }
}
