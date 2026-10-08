using Arcade.Libretro;
using SDL;
using static SDL.SDL3;

namespace Arcade.App;

/// <summary>
/// Maps the keyboard and SDL gamepads onto libretro's RetroPad, one port per player.
/// Keyboard drives player 1 (plus coin/start for player 2). Gamepads take ports in connection
/// order, so the first pad also drives player 1 alongside the keyboard.
/// </summary>
sealed unsafe class InputManager : IInputSource, IDisposable
{
    public const int MaxPlayers = 4;
    const short StickThreshold = 16384; // half deflection

    // RetroArch-style defaults plus MAME-style arcade keys, so either habit works.
    static readonly (SDL_Scancode Key, JoypadButton Button)[] Player1Keys =
    [
        (SDL_Scancode.SDL_SCANCODE_UP, JoypadButton.Up),
        (SDL_Scancode.SDL_SCANCODE_DOWN, JoypadButton.Down),
        (SDL_Scancode.SDL_SCANCODE_LEFT, JoypadButton.Left),
        (SDL_Scancode.SDL_SCANCODE_RIGHT, JoypadButton.Right),
        (SDL_Scancode.SDL_SCANCODE_Z, JoypadButton.B),          // button 1
        (SDL_Scancode.SDL_SCANCODE_X, JoypadButton.A),          // button 2
        (SDL_Scancode.SDL_SCANCODE_A, JoypadButton.Y),          // button 3
        (SDL_Scancode.SDL_SCANCODE_S, JoypadButton.X),          // button 4
        (SDL_Scancode.SDL_SCANCODE_Q, JoypadButton.L),          // button 5
        (SDL_Scancode.SDL_SCANCODE_W, JoypadButton.R),          // button 6
        (SDL_Scancode.SDL_SCANCODE_LCTRL, JoypadButton.B),
        (SDL_Scancode.SDL_SCANCODE_LALT, JoypadButton.A),
        (SDL_Scancode.SDL_SCANCODE_SPACE, JoypadButton.Y),
        (SDL_Scancode.SDL_SCANCODE_LSHIFT, JoypadButton.X),
        (SDL_Scancode.SDL_SCANCODE_5, JoypadButton.Select),     // insert coin
        (SDL_Scancode.SDL_SCANCODE_1, JoypadButton.Start),
        (SDL_Scancode.SDL_SCANCODE_RETURN, JoypadButton.Start),
    ];

    static readonly (SDL_Scancode Key, JoypadButton Button)[] Player2Keys =
    [
        (SDL_Scancode.SDL_SCANCODE_6, JoypadButton.Select),
        (SDL_Scancode.SDL_SCANCODE_2, JoypadButton.Start),
    ];

    static readonly (SDL_GamepadButton Pad, JoypadButton Button)[] PadButtons =
    [
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, JoypadButton.B),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, JoypadButton.A),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, JoypadButton.Y),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH, JoypadButton.X),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, JoypadButton.L),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, JoypadButton.R),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK, JoypadButton.L3),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK, JoypadButton.R3),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, JoypadButton.Select),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, JoypadButton.Start),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, JoypadButton.Up),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, JoypadButton.Down),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, JoypadButton.Left),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, JoypadButton.Right),
    ];

    readonly List<(SDL_JoystickID Id, nint Pad)> _pads = new();
    readonly ushort[] _buttons = new ushort[MaxPlayers];
    readonly short[,] _leftStick = new short[MaxPlayers, 2];
    readonly SDLBool* _keyboard;

    /// <summary>Raised with a short description whenever a gamepad connects or disconnects.</summary>
    public event Action<string>? Message;

    public InputManager()
    {
        _keyboard = SDL_GetKeyboardState(null); // valid for the lifetime of the app
        using var existing = SDL_GetGamepads();
        if (existing != null)
            foreach (var id in existing)
                AddGamepad(id);
    }

    public int GamepadCount => _pads.Count;

    public void AddGamepad(SDL_JoystickID id)
    {
        if (_pads.Any(p => p.Id == id))
            return;
        var pad = SDL_OpenGamepad(id);
        if (pad == null)
            return;
        _pads.Add((id, (nint)pad));
        Message?.Invoke($"Controller {_pads.Count} connected: {SDL_GetGamepadName(pad)}");
    }

    public void RemoveGamepad(SDL_JoystickID id)
    {
        var index = _pads.FindIndex(p => p.Id == id);
        if (index < 0)
            return;
        SDL_CloseGamepad((SDL_Gamepad*)_pads[index].Pad);
        _pads.RemoveAt(index);
        Message?.Invoke($"Controller {index + 1} disconnected");
    }

    /// <summary>Open gamepad handles (SDL_Gamepad*), for reading menu input from the same pads.</summary>
    public IEnumerable<nint> Pads => _pads.Select(p => p.Pad);

    /// <summary>True while any gamepad holds Back + Start together (the pad "menu" chord).</summary>
    public bool MenuChordHeld => _pads.Any(p =>
        SDL_GetGamepadButton((SDL_Gamepad*)p.Pad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK) &&
        SDL_GetGamepadButton((SDL_Gamepad*)p.Pad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START));

    /// <summary>True while any gamepad holds its Guide (Xbox / PS) button.</summary>
    public bool GuideHeld => _pads.Any(p => SDL_GetGamepadButton((SDL_Gamepad*)p.Pad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE));

    public void Poll()
    {
        Array.Clear(_buttons);
        Array.Clear(_leftStick);

        foreach (var (key, button) in Player1Keys)
            if (_keyboard[(int)key])
                _buttons[0] |= (ushort)(1 << (int)button);
        foreach (var (key, button) in Player2Keys)
            if (_keyboard[(int)key])
                _buttons[1] |= (ushort)(1 << (int)button);

        for (var port = 0; port < Math.Min(_pads.Count, MaxPlayers); port++)
        {
            var pad = (SDL_Gamepad*)_pads[port].Pad;
            ushort bits = 0;
            foreach (var (padButton, button) in PadButtons)
                if (SDL_GetGamepadButton(pad, padButton))
                    bits |= (ushort)(1 << (int)button);

            var x = SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX);
            var y = SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY);
            _leftStick[port, 0] = x;
            _leftStick[port, 1] = y;
            // The left stick also acts as a digital stick, which is what arcade games expect.
            if (x < -StickThreshold) bits |= 1 << (int)JoypadButton.Left;
            if (x > StickThreshold) bits |= 1 << (int)JoypadButton.Right;
            if (y < -StickThreshold) bits |= 1 << (int)JoypadButton.Up;
            if (y > StickThreshold) bits |= 1 << (int)JoypadButton.Down;
            if (SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER) > StickThreshold) bits |= 1 << (int)JoypadButton.L2;
            if (SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER) > StickThreshold) bits |= 1 << (int)JoypadButton.R2;

            _buttons[port] |= bits;
        }
    }

    public short GetState(uint port, uint device, uint index, uint id)
    {
        if (port >= MaxPlayers)
            return 0;
        return device switch
        {
            RetroDevice.Joypad when id < 16 => (short)((_buttons[port] >> (int)id) & 1),
            RetroDevice.Analog when index == 0 && id < 2 => _leftStick[port, id],
            _ => 0,
        };
    }

    public void Dispose()
    {
        foreach (var (_, pad) in _pads)
            SDL_CloseGamepad((SDL_Gamepad*)pad);
        _pads.Clear();
    }
}
