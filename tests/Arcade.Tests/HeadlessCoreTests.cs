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
    public void Run_ahead_shows_the_next_frame_on_fbneo() => RunAheadShowsLaterFrames("fbneo", "gridlee", 1);

    [RequiresCoreFact("fbneo", "gridlee")]
    public void Run_ahead_shows_two_frames_later_on_fbneo() => RunAheadShowsLaterFrames("fbneo", "gridlee", 2);

    [RequiresCoreFact("mame2003_plus", "robby")]
    public void Run_ahead_pictures_match_the_real_frames_on_mame2003_plus()
    {
        // Running ahead nudges something MAME 2003-Plus keeps outside save states: Robby Roto's
        // demo differs from a plain run after a few seconds, though for its first 15 seconds what's
        // shown is still what the game then really does, as the self-check confirms. (Later in the
        // demo the predictions do start to miss, and the self-check turns run-ahead off.)
        var input = new CountingInput();
        using var host = LoadCore("mame2003_plus", "robby", input);
        host.RunAhead = 1;
        Run(host, 900);
        Assert.Null(host.RunAheadProblem);
        Assert.True(host.RunAheadChecks > 20, $"{host.RunAheadChecks} self-checks");
        Assert.Equal(900, input.Polls);
    }

    [RequiresCoreFact("mame2003_plus", "supertnk")]
    public void Run_ahead_turns_itself_off_when_a_game_does_not_replay_exactly()
    {
        // MAME 2003-Plus's Super Tank doesn't come back exactly from a save state when run two
        // frames ahead, so what's shown isn't what then really happens; the self-check must notice.
        using var host = LoadCore("mame2003_plus", "supertnk", new CountingInput());
        Run(host, 200);
        host.RunAhead = 2;
        for (var i = 0; i < 1200 && host.RunAheadProblem == null; i++)
            host.RunFrame();
        Assert.NotNull(host.RunAheadProblem);
        output.WriteLine($"{host.RunAheadProblem} (after {host.FrameCount - 200} frames ahead, {host.RunAheadChecks} checks)");
    }
    [RequiresCoreFact("fbneo", "gridlee")]
    public void Timing_the_controls_measures_the_game_without_changing_it()
    {
        // Coin, start, then steer about: every change of the stick is timed.
        static ScriptedInput Script(Func<long> clock)
        {
            var input = new ScriptedInput(clock).Press(60, JoypadButton.Select).Press(120, JoypadButton.Start);
            for (var t = 300; t < 900; t += 50)
                input.Press(t, t / 50 % 2 == 0 ? JoypadButton.Left : JoypadButton.Up, 20);
            return input;
        }
        // Both plays start from one saved moment, as FBNeo seeds a byte from the clock when it loads.
        const int Frames = 900;
        var plain = new List<ulong>();
        var lags = new List<int>();
        long clock = 0;
        using var host = LoadCore("fbneo", "gridlee", Script(() => clock - 1));
        host.RunFrame();
        var start = host.SaveState();
        for (clock = 1; clock <= Frames; clock++)
        {
            host.RunFrame();
            plain.Add(host.LastFrame.ComputeHash());
        }

        host.LoadState(start);
        {
            host.MeasureLag = true;
            host.LagMeasured += lags.Add;
            for (clock = 1; clock <= Frames; clock++)
            {
                host.RunFrame();
                Assert.True(plain[(int)clock - 1] == host.LastFrame.ComputeHash(), $"frame {clock} changed by timing the controls");
            }
            Assert.Equal(0, host.UnrepeatableTimings);
        }
        output.WriteLine($"Gridlee answered the stick after: {string.Join(", ", lags)} frame(s)");
        Assert.True(lags.Count >= 10, $"only {lags.Count} timings");
        Assert.All(lags, lag => Assert.InRange(lag, 0, 7));
        Assert.Equal(1, lags.Min()); // Gridlee reads the controls a frame before it draws their effect
    }

    [RequiresCoreFact("mame2003_plus", "alienar")]
    public void Timings_that_do_not_replay_the_same_are_thrown_away()
    {
        // MAME 2003-Plus's Alien Arena doesn't come back exactly from a saved state, which would
        // look like the game answering the controls at once; such timings must not be reported.
        long clock = 0;
        var input = new ScriptedInput(() => clock);
        for (var t = 200; t < 1000; t += 40)
            input.Press(t, t / 40 % 2 == 0 ? JoypadButton.Left : JoypadButton.Up, 15);
        using var host = LoadCore("mame2003_plus", "alienar", input);
        var lags = new List<int>();
        host.MeasureLag = true;
        host.LagMeasured += lags.Add;
        for (; clock < 1000; clock++)
            host.RunFrame();
        output.WriteLine($"kept {lags.Count}, thrown away {host.UnrepeatableTimings}");
        Assert.True(host.UnrepeatableTimings >= 6);
        Assert.Empty(lags);
    }

    [RequiresCoreFact("mame2003_plus", "alienar")]
    public void Run_ahead_wrecks_Alien_Arena_on_MAME_2003_Plus_so_that_core_is_left_out()
    {
        // Each reload of a MAME 2003-Plus state leaves a little behind; run ahead every frame and
        // Alien Arena's boot goes wrong within seconds (the self-check can't see it: the frames shown
        // and the real ones are wrong alike). So run-ahead is only offered on cores that replay exactly.
        using var host = LoadCore("mame2003_plus", "alienar");
        host.RunFrame();
        var start = host.SaveState();
        Run(host, 300);
        var plain = host.LastFrame.ComputeHash();
        host.LoadState(start);
        host.RunAhead = 2;
        Run(host, 300);
        host.RunAhead = 0;
        Run(host, 1);
        Assert.NotEqual(plain, host.LastFrame.ComputeHash());
        Assert.False(Arcade.App.CoreCatalog.Find("mame2003_plus")!.RunAhead);
        Assert.True(Arcade.App.CoreCatalog.Find("fbneo")!.RunAhead);
    }

    sealed class CountingInput : IInputSource
    {
        public int Polls;
        public void Poll() => Polls++;
        public short GetState(uint port, uint device, uint index, uint id) => 0;
    }

    /// <summary>
    /// With no input, a game run <paramref name="ahead"/> frames ahead must show at frame k the
    /// picture the plain game shows at frame k + ahead, while its sound and input reads stay those
    /// of frame k.
    /// </summary>
    void RunAheadShowsLaterFrames(string core, string rom, int ahead)
    {
        // Both plays start from one saved moment, as FBNeo seeds a byte from the clock when it loads.
        const int Frames = 400;
        var input = new CountingInput();
        using var host = LoadCore(core, rom, input);
        Run(host, 200);
        var start = host.SaveState();
        var plain = new List<ulong>();
        var audio = host.AudioFramesReceived;
        for (var i = 0; i < Frames + ahead; i++)
        {
            host.RunFrame();
            plain.Add(host.LastFrame.ComputeHash());
            if (i == Frames - 1)
                audio = host.AudioFramesReceived - audio;
        }
        var moving = plain.Zip(plain.Skip(1)).Count(p => p.First != p.Second);
        Assert.True(moving > 50, $"the test needs a moving picture ({moving} changes)");

        host.LoadState(start);
        var polls = input.Polls;
        var audioBefore = host.AudioFramesReceived;
        host.RunAhead = ahead;
        for (var i = 0; i < Frames; i++)
        {
            host.RunFrame();
            Assert.True(plain[i + ahead] == host.LastFrame.ComputeHash(), $"frame {i} doesn't show frame {i + ahead}");
        }
        Assert.Null(host.RunAheadProblem);
        Assert.True(host.RunAheadChecks > 5, $"{host.RunAheadChecks} self-checks");
        Assert.Equal(Frames, input.Polls - polls); // hidden frames don't read the controls again
        Assert.Equal(audio, host.AudioFramesReceived - audioBefore); // only the real frames' sound
        output.WriteLine($"{core}/{rom}: {Frames} frames shown {ahead} ahead, {moving} picture changes, {host.RunAheadChecks} self-checks");
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
