using Arcade.Libretro;

namespace Arcade.App.Controls;

/// <summary>A connected gamepad or joystick, as the mapper sees it.</summary>
interface IRawDevice
{
    string Name { get; }
    /// <summary>True for devices SDL recognises as gamepads (uses <c>pad:</c> bindings), false for plain joysticks (<c>joy:</c>).</summary>
    bool IsGamepad { get; }
    bool IsDown(Binding binding);
    /// <summary>Left stick position, passed to games that read analog input.</summary>
    (short X, short Y) Stick { get; }
}

/// <summary>Keyboard and device state for one moment.</summary>
interface IRawInput
{
    bool IsKeyDown(int scancode);
    /// <summary>Devices in player order: the first drives player 1, the second player 2…</summary>
    IReadOnlyList<IRawDevice> Devices { get; }
}

/// <summary>
/// Turns raw keys and buttons into panel controls per player (using <see cref="ControlConfig"/>),
/// detects hotkeys, and produces what the core sees: RetroPad buttons with the game's own button
/// layout, picture rotation and free play applied. Pure logic, so it can be tested without SDL.
/// </summary>
sealed class ControlMapper
{
    // Free play: on Start, press coin for a few frames, wait for the credit to register, then press start.
    const int CoinFrames = 8, CreditGapFrames = 20, StartFrames = 8;

    readonly bool[,] _held = new bool[ArcadeControls.MaxPlayers, ArcadeControls.Count];
    readonly bool[] _anyHeld = new bool[ArcadeControls.Count];
    readonly bool[] _anyFromDevice = new bool[ArcadeControls.Count];
    readonly HashSet<Binding> _gameBindings = new();
    readonly float[] _comboTime = new float[Enum.GetValues<Hotkey>().Length];
    readonly bool[] _comboFired = new bool[Enum.GetValues<Hotkey>().Length];
    readonly bool[] _directWasDown = new bool[Enum.GetValues<Hotkey>().Length];
    readonly List<Hotkey> _fired = new();
    readonly ushort[] _retro = new ushort[ArcadeControls.MaxPlayers];
    readonly int[] _freePlayFrame = [-1, -1, -1, -1];
    readonly bool[] _startWasHeld = new bool[ArcadeControls.MaxPlayers];
    bool _enableHeld;

    public ControlMapper(ControlConfig config)
    {
        Config = config;
    }

    ControlConfig _config = null!;

    public ControlConfig Config
    {
        get => _config;
        set
        {
            _config = value;
            ConfigChanged();
        }
    }

    /// <summary>The running game's setup (button layout, rotation); empty in the game list.</summary>
    public GameSetup Game { get; set; } = new();
    /// <summary>Quarter turns clockwise the game picture is shown at; directions turn with it when <see cref="RotateControls"/> is on.</summary>
    public int PictureRotation { get; set; }
    public bool RotateControls { get; set; } = true;
    public bool FreePlay { get; set; }

    /// <summary>Hotkeys that fired in the last <see cref="Update"/>.</summary>
    public IReadOnlyList<Hotkey> FiredHotkeys => _fired;

    /// <summary>True while the hotkey-enable button is held.</summary>
    public bool EnableHeld => _enableHeld;

    /// <summary>Call after changing the config's bindings.</summary>
    public void ConfigChanged()
    {
        _gameBindings.Clear();
        foreach (var map in Config.Keyboard.Concat([Config.Gamepad, Config.Joystick]).Concat(Config.Devices.Values))
            foreach (var list in map.Values)
                _gameBindings.UnionWith(list);
    }

    /// <summary>
    /// Whether a hotkey binding needs the hotkey-enable button: it does when the same key or button
    /// also plays the game (e.g. Start), and doesn't when it's spare (e.g. Esc, F2, the Guide button).
    /// </summary>
    public bool NeedsEnable(Binding binding) => _gameBindings.Contains(binding);

    public bool Held(int player, ArcadeControl control) => _held[player, (int)control];

    /// <summary>True if any player holds the control; <paramref name="fromDevice"/> tells if a pad/joystick did.</summary>
    public bool AnyHeld(ArcadeControl control, out bool fromDevice)
    {
        fromDevice = _anyFromDevice[(int)control];
        return _anyHeld[(int)control];
    }

    /// <summary>True if the key is down, or any connected device has the button down.</summary>
    static bool IsDownAnywhere(IRawInput input, Binding binding, bool keyboard)
    {
        if (binding.IsKeyboard)
            return keyboard && input.IsKeyDown(binding.Code);
        foreach (var device in input.Devices)
            if (device.IsDown(binding))
                return true;
        return false;
    }

    static bool AnyDown(IRawInput input, IReadOnlyList<Binding> bindings, bool keyboard)
    {
        foreach (var binding in bindings)
            if (IsDownAnywhere(input, binding, keyboard))
                return true;
        return false;
    }

    /// <summary>Reads every player's panel controls. While hotkey-enable is held, buttons used for hotkeys don't reach the game.</summary>
    /// <param name="keyboard">False while typing in a text field.</param>
    public void ReadControls(IRawInput input, bool keyboard = true)
    {
        Array.Clear(_held);
        Array.Clear(_anyHeld);
        Array.Clear(_anyFromDevice);
        _enableHeld = AnyDown(input, Config.HotkeyEnable ?? [], keyboard);

        for (var player = 0; player < ArcadeControls.MaxPlayers; player++)
        {
            var keys = Config.PlayerKeys(player);
            var device = player < input.Devices.Count ? input.Devices[player] : null;
            var deviceMap = device == null ? null : Config.DeviceMap(device.Name, device.IsGamepad);
            for (var c = 0; c < ArcadeControls.Count; c++)
            {
                var control = (ArcadeControl)c;
                var held = false;
                var fromDevice = false;
                if (keyboard)
                    foreach (var b in keys.Get(control))
                        if (b.IsKeyboard && input.IsKeyDown(b.Code) && !Suppressed(b)) { held = true; break; }
                if (deviceMap != null)
                    foreach (var b in deviceMap.Get(control))
                        if (!b.IsKeyboard && device!.IsDown(b) && !Suppressed(b)) { held = fromDevice = true; break; }
                _held[player, c] = held;
                // Menus follow player 1's keys and every pad, so other players' keys don't move the cursor.
                if (held && (player == 0 || fromDevice))
                {
                    _anyHeld[c] = true;
                    _anyFromDevice[c] |= fromDevice;
                }
            }
        }
    }

    bool Suppressed(Binding binding)
    {
        if (!_enableHeld || (Config.HotkeyEnable?.Contains(binding) ?? false))
            return false;
        foreach (var list in Config.Hotkeys.Values)
            if (list.Contains(binding))
                return true;
        return false;
    }

    /// <summary>Once per app frame: reads controls and works out which hotkeys fired.</summary>
    public void Update(IRawInput input, float dt, bool keyboard = true)
    {
        ReadControls(input, keyboard);
        _fired.Clear();
        _fired.AddRange(_injected);
        _injected.Clear();
        foreach (var (hotkey, bindings) in Config.Hotkeys)
        {
            var h = (int)hotkey;
            bool direct = false, combo = false;
            foreach (var binding in bindings)
            {
                if (!IsDownAnywhere(input, binding, keyboard))
                    continue;
                if (NeedsEnable(binding))
                    combo |= _enableHeld;
                else
                    direct = true;
            }

            if (direct && !_directWasDown[h])
                _fired.Add(hotkey);
            _directWasDown[h] = direct;

            if (combo)
            {
                _comboTime[h] += dt;
                if (_comboTime[h] >= Config.ComboHoldSeconds && !_comboFired[h])
                {
                    _comboFired[h] = true;
                    if (!_fired.Contains(hotkey))
                        _fired.Add(hotkey);
                }
            }
            else
            {
                _comboTime[h] = 0;
                _comboFired[h] = false;
            }
        }
    }

    /// <summary>Progress (0–1) of the longest hotkey combination being held, for an on-screen hint.</summary>
    public float ComboProgress
    {
        get
        {
            var max = 0f;
            for (var i = 0; i < _comboTime.Length; i++)
                if (!_comboFired[i])
                    max = Math.Max(max, _comboTime[i]);
            return Config.ComboHoldSeconds <= 0 ? 0 : Math.Clamp(max / Config.ComboHoldSeconds, 0, 1);
        }
    }

    readonly List<Hotkey> _injected = new();

    /// <summary>Fires a hotkey on the next <see cref="Update"/> as if pressed (automation scripts).</summary>
    public void Inject(Hotkey hotkey) => _injected.Add(hotkey);

    /// <summary>Forgets held hotkeys, e.g. after a menu closes, so the button that closed it doesn't fire again.</summary>
    public void ResetHotkeys()
    {
        Array.Fill(_comboFired, true);
        Array.Clear(_comboTime);
        Array.Fill(_directWasDown, true);
    }

    /// <summary>
    /// Once per emulated frame: reads the controls and turns them into RetroPad buttons for each
    /// port, applying the game's button layout, picture rotation and free play.
    /// </summary>
    public void PollGame(IRawInput input)
    {
        ReadControls(input);
        for (var player = 0; player < ArcadeControls.MaxPlayers; player++)
        {
            ushort bits = 0;
            void Press(ArcadeControl control) => bits |= (ushort)(1 << (int)control.RetroButton());

            foreach (var direction in ArcadeControls.Directions)
                if (_held[player, (int)direction])
                    Press(RotateControls ? ArcadeControls.RotateDirection(direction, PictureRotation) : direction);

            for (var panel = 1; panel <= ArcadeControls.ButtonCount; panel++)
                if (_held[player, (int)ArcadeControls.Button(panel)] && Game.GameButton(panel) is var game and >= 1 and <= ArcadeControls.ButtonCount)
                    Press(ArcadeControls.Button(game));

            if (_held[player, (int)ArcadeControl.Coin])
                Press(ArcadeControl.Coin);

            var start = _held[player, (int)ArcadeControl.Start];
            if (FreePlay)
            {
                if (start && !_startWasHeld[player] && _freePlayFrame[player] < 0)
                    _freePlayFrame[player] = 0;
                var frame = _freePlayFrame[player];
                if (frame >= 0)
                {
                    if (frame < CoinFrames)
                        Press(ArcadeControl.Coin);
                    else if (frame >= CoinFrames + CreditGapFrames)
                        Press(ArcadeControl.Start);
                    _freePlayFrame[player] = ++frame >= CoinFrames + CreditGapFrames + StartFrames ? -1 : frame;
                }
                else if (start && _startWasHeld[player])
                {
                    Press(ArcadeControl.Start); // still held after the sequence: keep it held
                }
            }
            else if (start)
            {
                Press(ArcadeControl.Start);
            }
            _startWasHeld[player] = start;
            _retro[player] = bits;
        }
    }

    /// <summary>The RetroPad buttons for a port after the last <see cref="PollGame"/>.</summary>
    public ushort RetroButtons(int port) => port < _retro.Length ? _retro[port] : (ushort)0;
}
