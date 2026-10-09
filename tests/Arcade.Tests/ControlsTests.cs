using Arcade.App.Controls;
using Arcade.Libretro;
using SDL;

namespace Arcade.Tests;

/// <summary>Keyboard and devices for the mapper, without SDL.</summary>
sealed class FakeInput : IRawInput
{
    public readonly HashSet<SDL_Scancode> Keys = new();
    public readonly List<FakeDevice> DeviceList = new();
    public bool IsKeyDown(int scancode) => Keys.Contains((SDL_Scancode)scancode);
    public IReadOnlyList<IRawDevice> Devices => DeviceList;
}

sealed class FakeDevice(string name, bool isGamepad = true) : IRawDevice
{
    public readonly HashSet<Binding> Down = new();
    public string Name => name;
    public bool IsGamepad => isGamepad;
    public bool IsDown(Binding binding) => Down.Contains(binding);
    public (short X, short Y) Stick => (0, 0);
}

public class BindingTests
{
    [Theory]
    [InlineData("key:z")]
    [InlineData("key:lctrl")]
    [InlineData("key:f12")]
    [InlineData("key:kp_enter")]
    [InlineData("pad:south")]
    [InlineData("pad:dpad_up")]
    [InlineData("pad:left_shoulder")]
    [InlineData("pad:leftx-")]
    [InlineData("pad:right_trigger+")]
    [InlineData("joy:button0")]
    [InlineData("joy:button11")]
    [InlineData("joy:hat0up")]
    [InlineData("joy:hat1left")]
    [InlineData("joy:axis2+")]
    [InlineData("joy:axis0-")]
    public void Text_form_round_trips(string text)
    {
        Assert.True(Binding.TryParse(text, out var binding));
        Assert.Equal(text, binding.ToString());
    }

    [Fact]
    public void Parsing_ignores_case_and_spaces()
    {
        Assert.Equal(Binding.Key(SDL_Scancode.SDL_SCANCODE_LCTRL), Binding.Parse(" KEY:LCtrl "));
        Assert.Equal(Binding.Pad(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH), Binding.Parse("Pad:South"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("z")]
    [InlineData("key:")]
    [InlineData("key:notakey")]
    [InlineData("key:unknown")]
    [InlineData("pad:count")]
    [InlineData("pad:leftx")]
    [InlineData("pad:south+")]
    [InlineData("joy:button")]
    [InlineData("joy:buttonx")]
    [InlineData("joy:hat0")]
    [InlineData("joy:hatup")]
    [InlineData("joy:axis1")]
    [InlineData("mouse:left")]
    public void Rejects_what_is_not_a_key_or_button(string text)
    {
        Assert.False(Binding.TryParse(text, out _));
    }

    [Fact]
    public void Labels_are_short_and_readable()
    {
        Assert.Equal("Z", Binding.Parse("key:z").Label);
        Assert.Equal("L-Ctrl", Binding.Parse("key:lctrl").Label);
        Assert.Equal("F2", Binding.Parse("key:f2").Label);
        Assert.Equal("Pad Ⓐ", Binding.Parse("pad:south").Label);
        Assert.Equal("Stick ←", Binding.Parse("pad:leftx-").Label);
        Assert.Equal("Joy 4", Binding.Parse("joy:button3").Label);
        Assert.Equal("Joy ↑", Binding.Parse("joy:hat0up").Label);
    }
}

public class ControlConfigTests
{
    [Fact]
    public void Missing_file_gives_the_defaults()
    {
        var config = ControlConfig.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.Equal([Binding.Parse("key:z"), Binding.Parse("key:lctrl")], config.PlayerKeys(0).Get(ArcadeControl.Button1));
        Assert.Equal([Binding.Parse("key:6")], config.PlayerKeys(1).Get(ArcadeControl.Coin));
        Assert.Contains(Binding.Parse("pad:start"), config.HotkeyBindings(Hotkey.Menu));
        Assert.Equal([Binding.Parse("pad:back")], config.HotkeyEnable);
    }

    [Fact]
    public void File_only_needs_what_changed_and_an_empty_list_unbinds()
    {
        var config = ControlConfig.FromJson("""
            {
              // comments are allowed
              "keyboard": [ { "Button1": ["key:space"], "Coin": [] } ],
              "hotkeys": { "Menu": ["key:tab"] },
              "games": { "sf2": { "buttons": { "1": 4 }, "rotation": 1 } }
            }
            """);
        Assert.Equal([Binding.Parse("key:space")], config.PlayerKeys(0).Get(ArcadeControl.Button1));
        Assert.Empty(config.PlayerKeys(0).Get(ArcadeControl.Coin));
        Assert.Equal([Binding.Parse("key:x"), Binding.Parse("key:lalt")], config.PlayerKeys(0).Get(ArcadeControl.Button2)); // default kept
        Assert.Equal([Binding.Parse("key:6")], config.PlayerKeys(1).Get(ArcadeControl.Coin)); // other players default
        Assert.Equal([Binding.Parse("key:tab")], config.HotkeyBindings(Hotkey.Menu));
        Assert.Contains(Binding.Parse("key:f2"), config.HotkeyBindings(Hotkey.SaveState));
        Assert.Equal(4, config.Game("SF2").GameButton(1));
        Assert.Equal(2, config.Game("sf2").GameButton(2));
        Assert.Equal(1, config.Game("sf2").Rotation);
    }

    [Fact]
    public void Invalid_entries_are_dropped_with_a_warning()
    {
        var warnings = new List<string>();
        var config = ControlConfig.FromJson("""{ "gamepad": { "Button1": ["pad:south", "pad:nonsense"] } }""", warnings.Add);
        Assert.Equal([Binding.Parse("pad:south")], config.Gamepad.Get(ArcadeControl.Button1));
        Assert.Single(warnings);
    }

    [Fact]
    public void Saves_and_loads_the_same_setup()
    {
        var config = ControlConfig.Defaults();
        config.PlayerKeys(0)[ArcadeControl.Start] = [Binding.Parse("key:kp_enter")];
        config.Devices["Arcade Encoder"] = new ControlMap { [ArcadeControl.Button1] = [Binding.Parse("joy:button5")] };
        config.Games["robby"] = new GameSetup { Buttons = { [1] = 2, [2] = 1 } };
        config.Games["empty"] = new GameSetup();
        config.ComboHoldSeconds = 1;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            config.Save(path);
            var loaded = ControlConfig.Load(path);
            Assert.Equal([Binding.Parse("key:kp_enter")], loaded.PlayerKeys(0).Get(ArcadeControl.Start));
            Assert.Equal([Binding.Parse("joy:button5")], loaded.DeviceMap("arcade encoder", false).Get(ArcadeControl.Button1));
            Assert.Equal(2, loaded.Game("robby").GameButton(1));
            Assert.False(loaded.Games.ContainsKey("empty")); // nothing to remember
            Assert.Equal(1, loaded.ComboHoldSeconds);
            Assert.Contains("\"key:kp_enter\"", File.ReadAllText(path)); // readable, hand-editable
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ControlMapperTests
{
    static Binding Key(string name) => Binding.Parse("key:" + name);
    static Binding Pad(string name) => Binding.Parse("pad:" + name);

    static bool RetroDown(ControlMapper mapper, int port, JoypadButton button) => (mapper.RetroButtons(port) & (1 << (int)button)) != 0;

    [Fact]
    public void Keyboard_and_pads_drive_their_players()
    {
        var mapper = new ControlMapper(ControlConfig.Defaults());
        var input = new FakeInput();
        var pad1 = new FakeDevice("Xbox Controller");
        var pad2 = new FakeDevice("Xbox Controller");
        input.DeviceList.AddRange([pad1, pad2]);

        input.Keys.Add(SDL_Scancode.SDL_SCANCODE_Z);           // player 1 button 1
        input.Keys.Add(SDL_Scancode.SDL_SCANCODE_2);           // player 2 start
        pad2.Down.Add(Pad("east"));                            // player 2 button 2
        pad2.Down.Add(Binding.PadAxis(SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX, -1)); // player 2 left
        mapper.PollGame(input);

        Assert.True(RetroDown(mapper, 0, JoypadButton.B));
        Assert.Equal(1 << (int)JoypadButton.B, mapper.RetroButtons(0));
        Assert.True(RetroDown(mapper, 1, JoypadButton.Start));
        Assert.True(RetroDown(mapper, 1, JoypadButton.A));
        Assert.True(RetroDown(mapper, 1, JoypadButton.Left));
        Assert.Equal(0, mapper.RetroButtons(2));
    }

    [Fact]
    public void A_device_profile_replaces_the_shared_layout_for_that_device_only()
    {
        var config = ControlConfig.Defaults();
        config.Devices["Arcade Stick"] = new ControlMap { [ArcadeControl.Button1] = [Pad("north")] };
        var mapper = new ControlMapper(config);
        var input = new FakeInput();
        var stick = new FakeDevice("Arcade Stick");
        var pad = new FakeDevice("Xbox Controller");
        input.DeviceList.AddRange([stick, pad]);
        stick.Down.Add(Pad("north"));
        pad.Down.Add(Pad("north"));
        mapper.PollGame(input);

        Assert.True(RetroDown(mapper, 0, JoypadButton.B));  // stick: north is button 1
        Assert.True(RetroDown(mapper, 1, JoypadButton.X));  // pad: north is button 4 as usual
    }

    [Fact]
    public void Joysticks_use_the_joystick_layout()
    {
        var mapper = new ControlMapper(ControlConfig.Defaults());
        var input = new FakeInput();
        var encoder = new FakeDevice("USB Encoder", isGamepad: false);
        input.DeviceList.Add(encoder);
        encoder.Down.Add(new Binding(BindingKind.JoyHat, 0, Binding.HatUp));
        encoder.Down.Add(new Binding(BindingKind.JoyButton, 2));
        encoder.Down.Add(new Binding(BindingKind.JoyButton, 8));
        mapper.PollGame(input);
        Assert.Equal((1 << (int)JoypadButton.Up) | (1 << (int)JoypadButton.Y) | (1 << (int)JoypadButton.Select), mapper.RetroButtons(0));
    }

    [Fact]
    public void Game_button_layout_and_picture_rotation_apply_to_what_the_core_sees()
    {
        var mapper = new ControlMapper(ControlConfig.Defaults())
        {
            Game = new GameSetup { Buttons = { [1] = 3, [2] = 0 } },
            PictureRotation = 1, // picture turned right: pushing right is the game's up
        };
        var input = new FakeInput();
        input.Keys.UnionWith([SDL_Scancode.SDL_SCANCODE_Z, SDL_Scancode.SDL_SCANCODE_X, SDL_Scancode.SDL_SCANCODE_RIGHT]);
        mapper.PollGame(input);
        Assert.Equal((1 << (int)JoypadButton.Y) | (1 << (int)JoypadButton.Up), mapper.RetroButtons(0));

        mapper.RotateControls = false;
        mapper.PollGame(input);
        Assert.True(RetroDown(mapper, 0, JoypadButton.Right));
    }

    [Theory]
    [InlineData("Up", 1, "Left")]
    [InlineData("Right", 1, "Up")]
    [InlineData("Up", 2, "Down")]
    [InlineData("Up", 3, "Right")]
    [InlineData("Left", 0, "Left")]
    public void Directions_turn_against_the_picture(string pushed, int turns, string game)
    {
        Assert.Equal(Enum.Parse<ArcadeControl>(game), ArcadeControls.RotateDirection(Enum.Parse<ArcadeControl>(pushed), turns));
    }

    [Fact]
    public void Free_play_inserts_a_coin_then_presses_start()
    {
        var mapper = new ControlMapper(ControlConfig.Defaults()) { FreePlay = true };
        var input = new FakeInput();
        input.Keys.Add(SDL_Scancode.SDL_SCANCODE_1);
        var frames = new List<ushort>();
        for (var i = 0; i < 40; i++)
        {
            if (i == 3)
                input.Keys.Clear(); // a quick tap on start
            mapper.PollGame(input);
            frames.Add(mapper.RetroButtons(0));
        }
        var coin = (ushort)(1 << (int)JoypadButton.Select);
        var start = (ushort)(1 << (int)JoypadButton.Start);
        Assert.Equal(coin, frames[0]);
        var firstStart = frames.FindIndex(f => f == start);
        var lastCoin = frames.FindLastIndex(f => f == coin);
        Assert.True(firstStart > lastCoin + 10, "the credit needs time to register before start");
        Assert.Equal(0, frames[^1]);
        Assert.DoesNotContain(frames, f => f == (coin | start));
    }

    [Fact]
    public void Spare_keys_are_hotkeys_on_their_own_and_fire_once_per_press()
    {
        var mapper = new ControlMapper(ControlConfig.Defaults());
        var input = new FakeInput();
        input.Keys.Add(SDL_Scancode.SDL_SCANCODE_F2);
        mapper.Update(input, 0.016f);
        Assert.Equal([Hotkey.SaveState], mapper.FiredHotkeys);
        mapper.Update(input, 0.016f);
        Assert.Empty(mapper.FiredHotkeys);
        input.Keys.Clear();
        mapper.Update(input, 0.016f);
        input.Keys.Add(SDL_Scancode.SDL_SCANCODE_ESCAPE);
        mapper.Update(input, 0.016f);
        Assert.Equal([Hotkey.Menu], mapper.FiredHotkeys);
    }

    [Fact]
    public void Game_buttons_need_hotkey_enable_held_for_a_moment_and_are_kept_from_the_game_meanwhile()
    {
        var mapper = new ControlMapper(ControlConfig.Defaults());
        var input = new FakeInput();
        var pad = new FakeDevice("Xbox Controller");
        input.DeviceList.Add(pad);
        Assert.True(mapper.NeedsEnable(Pad("start")));
        Assert.False(mapper.NeedsEnable(Pad("guide")));
        Assert.False(mapper.NeedsEnable(Key("escape")));

        // Start alone plays the game.
        pad.Down.Add(Pad("start"));
        mapper.Update(input, 0.1f);
        Assert.Empty(mapper.FiredHotkeys);
        mapper.PollGame(input);
        Assert.True(RetroDown(mapper, 0, JoypadButton.Start));

        // Back (hotkey enable) + Start: the game sees the coin but not start, and the menu opens after the hold.
        pad.Down.Add(Pad("back"));
        mapper.PollGame(input);
        Assert.False(RetroDown(mapper, 0, JoypadButton.Start));
        Assert.True(RetroDown(mapper, 0, JoypadButton.Select));
        mapper.Update(input, 0.3f);
        Assert.Empty(mapper.FiredHotkeys);
        mapper.Update(input, 0.3f);
        Assert.Equal([Hotkey.Menu], mapper.FiredHotkeys);
        mapper.Update(input, 0.3f);
        Assert.Empty(mapper.FiredHotkeys); // once per hold
    }

    [Fact]
    public void Rewind_and_fast_forward_last_while_held()
    {
        var mapper = new ControlMapper(ControlConfig.Defaults());
        var input = new FakeInput();
        input.Keys.Add(SDL_Scancode.SDL_SCANCODE_BACKSPACE);
        mapper.Update(input, 0.016f);
        Assert.True(mapper.IsHeld(Hotkey.Rewind));
        mapper.Update(input, 0.016f);
        Assert.True(mapper.IsHeld(Hotkey.Rewind));
        Assert.False(mapper.IsHeld(Hotkey.FastForward));
        input.Keys.Clear();
        mapper.Update(input, 0.016f);
        Assert.False(mapper.IsHeld(Hotkey.Rewind));

        // On a pad the trigger is a game button, so it rewinds only with hotkey enable held, after the hold time.
        var pad = new FakeDevice("Xbox Controller");
        input.DeviceList.Add(pad);
        pad.Down.Add(Binding.Parse("pad:left_trigger+"));
        mapper.Update(input, 0.3f);
        Assert.False(mapper.IsHeld(Hotkey.Rewind));
        pad.Down.Add(Pad("back"));
        mapper.Update(input, 0.3f);
        Assert.False(mapper.IsHeld(Hotkey.Rewind));
        mapper.Update(input, 0.3f);
        Assert.True(mapper.IsHeld(Hotkey.Rewind));
    }

    [Fact]
    public void A_key_still_held_when_a_menu_closes_does_not_start_rewinding()
    {
        var mapper = new ControlMapper(ControlConfig.Defaults());
        var input = new FakeInput();
        input.Keys.Add(SDL_Scancode.SDL_SCANCODE_BACKSPACE); // Backspace also closes menus
        mapper.Update(input, 0.016f);
        mapper.ResetHotkeys();
        mapper.Update(input, 0.016f);
        Assert.False(mapper.IsHeld(Hotkey.Rewind));
        input.Keys.Clear();
        mapper.Update(input, 0.016f);
        input.Keys.Add(SDL_Scancode.SDL_SCANCODE_BACKSPACE);
        mapper.Update(input, 0.016f);
        Assert.True(mapper.IsHeld(Hotkey.Rewind));
    }

    [Fact]
    public void Menus_follow_player_one_and_every_pad_but_not_other_players_keys()
    {
        var mapper = new ControlMapper(ControlConfig.Defaults());
        var input = new FakeInput();
        input.Keys.Add(SDL_Scancode.SDL_SCANCODE_I); // player 2 button 1
        mapper.ReadControls(input);
        Assert.True(mapper.Held(1, ArcadeControl.Button1));
        Assert.False(mapper.AnyHeld(ArcadeControl.Button1, out _));

        var pad = new FakeDevice("Pad");
        input.DeviceList.AddRange([new FakeDevice("Pad"), pad]); // player 2's pad
        pad.Down.Add(Pad("south"));
        mapper.ReadControls(input);
        Assert.True(mapper.AnyHeld(ArcadeControl.Button1, out var fromDevice));
        Assert.True(fromDevice);
    }

    [Fact]
    public void Typing_in_a_text_field_turns_keyboard_controls_off()
    {
        var mapper = new ControlMapper(ControlConfig.Defaults());
        var input = new FakeInput();
        input.Keys.Add(SDL_Scancode.SDL_SCANCODE_Z);
        mapper.ReadControls(input, keyboard: false);
        Assert.False(mapper.Held(0, ArcadeControl.Button1));
    }
}
