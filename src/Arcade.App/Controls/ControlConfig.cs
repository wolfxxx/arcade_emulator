using System.Text.Json;
using System.Text.Json.Serialization;
using SDL;

namespace Arcade.App.Controls;

/// <summary>Bindings for each control of one player or one device.</summary>
sealed class ControlMap : Dictionary<ArcadeControl, List<Binding>>
{
    public ControlMap() { }
    public ControlMap(ControlMap other) : base(other.ToDictionary(kv => kv.Key, kv => kv.Value.ToList())) { }

    public IReadOnlyList<Binding> Get(ArcadeControl control) => TryGetValue(control, out var list) ? list : [];
}

/// <summary>A game's own setup: which panel button presses which game button, and how its picture is turned.</summary>
sealed class GameSetup
{
    /// <summary>Panel button → game button, for buttons that differ from the panel's numbering.</summary>
    public Dictionary<int, int> Buttons { get; set; } = new();
    /// <summary>Quarter turns clockwise to show the picture at, or null for the default (upright, or the vertical-games setting).</summary>
    public int? Rotation { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Buttons.Count == 0 && Rotation == null;

    /// <summary>The game button a panel button presses (1–8), or 0 if it does nothing in this game.</summary>
    public int GameButton(int panelButton) => Buttons.TryGetValue(panelButton, out var game) ? game : panelButton;
}

/// <summary>
/// Every key and button assignment, stored as controls.json next to the app. Anything missing
/// from the file falls back to the defaults, so the file only needs what someone changed.
/// </summary>
sealed class ControlConfig
{
    /// <summary>Keyboard keys per player (index 0 = player 1). Arcade encoders that act as keyboards go here too.</summary>
    public ControlMap[] Keyboard { get; set; } = new ControlMap[ArcadeControls.MaxPlayers];
    /// <summary>Layout for any recognised gamepad without its own profile.</summary>
    public ControlMap Gamepad { get; set; } = new();
    /// <summary>Layout for any other joystick (e.g. a USB encoder) without its own profile.</summary>
    public ControlMap Joystick { get; set; } = new();
    /// <summary>Profiles for particular devices by name, replacing <see cref="Gamepad"/>/<see cref="Joystick"/> for them.</summary>
    public Dictionary<string, ControlMap> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<Hotkey, List<Binding>> Hotkeys { get; set; } = new();
    /// <summary>
    /// Hotkeys on buttons that also play the game (e.g. Start for the pause menu) only work while
    /// this is held, so a panel with few buttons can still reach them.
    /// </summary>
    public List<Binding>? HotkeyEnable { get; set; }
    /// <summary>How long a hotkey-enable combination must be held, so normal play (coin then start) doesn't trigger it.</summary>
    public float ComboHoldSeconds { get; set; } = 0.5f;
    /// <summary>Per-game setups by set name.</summary>
    public Dictionary<string, GameSetup> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ControlMap PlayerKeys(int player) => Keyboard[player] ??= new ControlMap();

    /// <summary>The layout a device uses: its own profile if it has one, else the shared one for its kind.</summary>
    public ControlMap DeviceMap(string name, bool isGamepad) =>
        Devices.TryGetValue(name, out var own) ? own : isGamepad ? Gamepad : Joystick;

    public GameSetup Game(string setName) => Games.TryGetValue(setName, out var setup) ? setup : new GameSetup();

    public IReadOnlyList<Binding> HotkeyBindings(Hotkey hotkey) => Hotkeys.TryGetValue(hotkey, out var list) ? list : [];

    // ---- Defaults ----

    static Binding K(SDL_Scancode key) => Binding.Key(key);
    static Binding P(SDL_GamepadButton button) => Binding.Pad(button);

    static ControlMap Map(params (ArcadeControl Control, Binding[] Bindings)[] entries)
    {
        var map = new ControlMap();
        foreach (var (control, bindings) in entries)
            map[control] = bindings.ToList();
        return map;
    }

    /// <summary>RetroArch-style keys plus the MAME arcade keys (Ctrl/Alt/Space/Shift), so either habit works.</summary>
    public static ControlMap DefaultKeyboard(int player) => player switch
    {
        0 => Map(
            (ArcadeControl.Up, [K(SDL_Scancode.SDL_SCANCODE_UP)]),
            (ArcadeControl.Down, [K(SDL_Scancode.SDL_SCANCODE_DOWN)]),
            (ArcadeControl.Left, [K(SDL_Scancode.SDL_SCANCODE_LEFT)]),
            (ArcadeControl.Right, [K(SDL_Scancode.SDL_SCANCODE_RIGHT)]),
            (ArcadeControl.Button1, [K(SDL_Scancode.SDL_SCANCODE_Z), K(SDL_Scancode.SDL_SCANCODE_LCTRL)]),
            (ArcadeControl.Button2, [K(SDL_Scancode.SDL_SCANCODE_X), K(SDL_Scancode.SDL_SCANCODE_LALT)]),
            (ArcadeControl.Button3, [K(SDL_Scancode.SDL_SCANCODE_A), K(SDL_Scancode.SDL_SCANCODE_SPACE)]),
            (ArcadeControl.Button4, [K(SDL_Scancode.SDL_SCANCODE_S), K(SDL_Scancode.SDL_SCANCODE_LSHIFT)]),
            (ArcadeControl.Button5, [K(SDL_Scancode.SDL_SCANCODE_Q)]),
            (ArcadeControl.Button6, [K(SDL_Scancode.SDL_SCANCODE_W)]),
            (ArcadeControl.Button7, [K(SDL_Scancode.SDL_SCANCODE_E)]),
            (ArcadeControl.Button8, [K(SDL_Scancode.SDL_SCANCODE_D)]),
            (ArcadeControl.Coin, [K(SDL_Scancode.SDL_SCANCODE_5)]),
            (ArcadeControl.Start, [K(SDL_Scancode.SDL_SCANCODE_1), K(SDL_Scancode.SDL_SCANCODE_RETURN)])),
        // Player 2: R F G H for the stick and I O K L for buttons, away from player 1's keys.
        1 => Map(
            (ArcadeControl.Up, [K(SDL_Scancode.SDL_SCANCODE_R)]),
            (ArcadeControl.Down, [K(SDL_Scancode.SDL_SCANCODE_F)]),
            (ArcadeControl.Left, [K(SDL_Scancode.SDL_SCANCODE_G)]),
            (ArcadeControl.Right, [K(SDL_Scancode.SDL_SCANCODE_H)]),
            (ArcadeControl.Button1, [K(SDL_Scancode.SDL_SCANCODE_I)]),
            (ArcadeControl.Button2, [K(SDL_Scancode.SDL_SCANCODE_O)]),
            (ArcadeControl.Button3, [K(SDL_Scancode.SDL_SCANCODE_K)]),
            (ArcadeControl.Button4, [K(SDL_Scancode.SDL_SCANCODE_L)]),
            (ArcadeControl.Coin, [K(SDL_Scancode.SDL_SCANCODE_6)]),
            (ArcadeControl.Start, [K(SDL_Scancode.SDL_SCANCODE_2)])),
        2 => Map((ArcadeControl.Coin, [K(SDL_Scancode.SDL_SCANCODE_7)]), (ArcadeControl.Start, [K(SDL_Scancode.SDL_SCANCODE_3)])),
        3 => Map((ArcadeControl.Coin, [K(SDL_Scancode.SDL_SCANCODE_8)]), (ArcadeControl.Start, [K(SDL_Scancode.SDL_SCANCODE_4)])),
        _ => new ControlMap(),
    };

    /// <summary>Face buttons as 1–4, shoulders 5–6, triggers 7–8; Back inserts a coin. D-pad and left stick both move.</summary>
    public static ControlMap DefaultGamepad() => Map(
        (ArcadeControl.Up, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP), Binding.PadAxis(SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY, -1)]),
        (ArcadeControl.Down, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN), Binding.PadAxis(SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY, 1)]),
        (ArcadeControl.Left, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT), Binding.PadAxis(SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX, -1)]),
        (ArcadeControl.Right, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT), Binding.PadAxis(SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX, 1)]),
        (ArcadeControl.Button1, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH)]),
        (ArcadeControl.Button2, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST)]),
        (ArcadeControl.Button3, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST)]),
        (ArcadeControl.Button4, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH)]),
        (ArcadeControl.Button5, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER)]),
        (ArcadeControl.Button6, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER)]),
        (ArcadeControl.Button7, [Binding.PadAxis(SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER, 1)]),
        (ArcadeControl.Button8, [Binding.PadAxis(SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER, 1)]),
        (ArcadeControl.Coin, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK)]),
        (ArcadeControl.Start, [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START)]));

    /// <summary>Typical USB encoder: hat or first two axes for the stick, buttons 1–8 in order, then coin and start.</summary>
    public static ControlMap DefaultJoystick() => Map(
        (ArcadeControl.Up, [new(BindingKind.JoyHat, 0, Binding.HatUp), new(BindingKind.JoyAxis, 1, -1)]),
        (ArcadeControl.Down, [new(BindingKind.JoyHat, 0, Binding.HatDown), new(BindingKind.JoyAxis, 1, 1)]),
        (ArcadeControl.Left, [new(BindingKind.JoyHat, 0, Binding.HatLeft), new(BindingKind.JoyAxis, 0, -1)]),
        (ArcadeControl.Right, [new(BindingKind.JoyHat, 0, Binding.HatRight), new(BindingKind.JoyAxis, 0, 1)]),
        (ArcadeControl.Button1, [new(BindingKind.JoyButton, 0)]),
        (ArcadeControl.Button2, [new(BindingKind.JoyButton, 1)]),
        (ArcadeControl.Button3, [new(BindingKind.JoyButton, 2)]),
        (ArcadeControl.Button4, [new(BindingKind.JoyButton, 3)]),
        (ArcadeControl.Button5, [new(BindingKind.JoyButton, 4)]),
        (ArcadeControl.Button6, [new(BindingKind.JoyButton, 5)]),
        (ArcadeControl.Button7, [new(BindingKind.JoyButton, 6)]),
        (ArcadeControl.Button8, [new(BindingKind.JoyButton, 7)]),
        (ArcadeControl.Coin, [new(BindingKind.JoyButton, 8)]),
        (ArcadeControl.Start, [new(BindingKind.JoyButton, 9)]));

    public static Dictionary<Hotkey, List<Binding>> DefaultHotkeys() => new()
    {
        [Hotkey.Menu] = [K(SDL_Scancode.SDL_SCANCODE_ESCAPE), K(SDL_Scancode.SDL_SCANCODE_P), K(SDL_Scancode.SDL_SCANCODE_PAUSE),
                         P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE), P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START)],
        [Hotkey.SaveState] = [K(SDL_Scancode.SDL_SCANCODE_F2), P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER)],
        [Hotkey.LoadState] = [K(SDL_Scancode.SDL_SCANCODE_F4), P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER)],
        [Hotkey.Reset] = [K(SDL_Scancode.SDL_SCANCODE_F3)],
        [Hotkey.Screenshot] = [K(SDL_Scancode.SDL_SCANCODE_F12)],
        [Hotkey.ExitGame] = [],
    };

    public static List<Binding> DefaultHotkeyEnable() => [P(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK)];

    public static ControlConfig Defaults()
    {
        var config = new ControlConfig();
        config.FillMissing();
        return config;
    }

    /// <summary>Puts defaults wherever the file said nothing (a control listed with no bindings stays unbound).</summary>
    void FillMissing()
    {
        if (Keyboard.Length != ArcadeControls.MaxPlayers)
            Keyboard = Keyboard.Concat(new ControlMap[ArcadeControls.MaxPlayers]).Take(ArcadeControls.MaxPlayers).ToArray();
        for (var p = 0; p < ArcadeControls.MaxPlayers; p++)
            Keyboard[p] = Merge(Keyboard[p], DefaultKeyboard(p));
        Gamepad = Merge(Gamepad, DefaultGamepad());
        Joystick = Merge(Joystick, DefaultJoystick());
        var hotkeys = DefaultHotkeys();
        foreach (var (hotkey, bindings) in Hotkeys)
            hotkeys[hotkey] = bindings;
        Hotkeys = hotkeys;
        Devices = new Dictionary<string, ControlMap>(Devices ?? new(), StringComparer.OrdinalIgnoreCase);
        Games = new Dictionary<string, GameSetup>(Games ?? new(), StringComparer.OrdinalIgnoreCase);
        HotkeyEnable ??= DefaultHotkeyEnable();
    }

    static ControlMap Merge(ControlMap? map, ControlMap defaults)
    {
        if (map == null)
            return defaults;
        foreach (var (control, bindings) in defaults)
            map.TryAdd(control, bindings);
        return map;
    }

    /// <summary>Resets one player's keyboard keys to the defaults.</summary>
    public void ResetKeyboard(int player) => Keyboard[player] = DefaultKeyboard(player);

    // ---- File ----

    sealed class BindingConverter : JsonConverter<Binding>
    {
        public override Binding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Binding.TryParse(reader.GetString(), out var binding) ? binding : default;

        public override void Write(Utf8JsonWriter writer, Binding value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new BindingConverter(), new JsonStringEnumConverter() },
    };

    /// <summary>Loads controls.json; a missing or broken file gives the defaults (with a warning for a broken one).</summary>
    public static ControlConfig Load(string path, Action<string>? warn = null)
    {
        ControlConfig? config = null;
        try
        {
            if (File.Exists(path))
                config = JsonSerializer.Deserialize<ControlConfig>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException e)
        {
            warn?.Invoke($"controls.json could not be read, using default controls ({e.Message})");
        }
        config ??= new ControlConfig();
        config.DropInvalid(warn);
        config.FillMissing();
        return config;
    }

    /// <summary>Unreadable bindings come through the converter as default(Binding) (an unknown key); drop them.</summary>
    void DropInvalid(Action<string>? warn)
    {
        Keyboard ??= new ControlMap[ArcadeControls.MaxPlayers];
        var dropped = 0;
        void Clean(List<Binding>? list) => dropped += list?.RemoveAll(b => b == default) ?? 0;
        foreach (var map in Keyboard.Where(m => m != null).Concat([Gamepad, Joystick]).Concat(Devices?.Values ?? Enumerable.Empty<ControlMap>()))
            if (map != null)
                foreach (var list in map.Values)
                    Clean(list);
        foreach (var list in Hotkeys?.Values ?? Enumerable.Empty<List<Binding>>())
            Clean(list);
        Clean(HotkeyEnable);
        Hotkeys ??= new();
        if (dropped > 0)
            warn?.Invoke($"controls.json: ignored {dropped} entr{(dropped == 1 ? "y" : "ies")} that aren't keys or buttons");
    }

    public void Save(string path)
    {
        try
        {
            Games = Games.Where(g => !g.Value.IsEmpty).ToDictionary(StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"Could not save controls: {e.Message}");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ControlConfig FromJson(string json, Action<string>? warn = null)
    {
        var config = JsonSerializer.Deserialize<ControlConfig>(json, JsonOptions) ?? new ControlConfig();
        config.DropInvalid(warn);
        config.FillMissing();
        return config;
    }
}
