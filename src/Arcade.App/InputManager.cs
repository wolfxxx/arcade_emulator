using Arcade.App.Controls;
using Arcade.Libretro;
using SDL;
using static SDL.SDL3;

namespace Arcade.App;

/// <summary>
/// Owns the keyboard and every connected gamepad or joystick, and feeds them through a
/// <see cref="ControlMapper"/> to the core (one port per player), the menus and the hotkeys.
/// Devices take players in connection order; the keyboard has its own keys for each player.
/// </summary>
sealed unsafe class InputManager : IInputSource, IRawInput, IDisposable
{
    const short AxisThreshold = 16384;   // half deflection counts as pressed
    const short CaptureThreshold = 24000; // capturing needs a firmer push, so a resting stick isn't picked up

    readonly List<SdlDevice> _devices = new();
    readonly SDLBool* _keyboard;

    public ControlMapper Mapper { get; }

    /// <summary>Raised with a short description whenever a device connects or disconnects.</summary>
    public event Action<string>? Message;

    public InputManager(ControlConfig config)
    {
        Mapper = new ControlMapper(config);
        _keyboard = SDL_GetKeyboardState(null); // valid for the lifetime of the app
        using var existing = SDL_GetJoysticks();
        if (existing != null)
            foreach (var id in existing)
                AddDevice(id, announce: false);
    }

    public IReadOnlyList<IRawDevice> Devices => _devices;
    public bool IsKeyDown(int scancode) => scancode is >= 0 and < (int)SDL_Scancode.SDL_SCANCODE_COUNT && _keyboard[scancode];

    /// <summary>Name of the device driving a player, or null if none is connected for them.</summary>
    public string? DeviceName(int player) => player < _devices.Count ? _devices[player].Name : null;

    public void AddDevice(SDL_JoystickID id, bool announce = true)
    {
        if (_devices.Any(d => d.Id == id))
            return;
        SdlDevice device;
        if (SDL_IsGamepad(id))
        {
            var pad = SDL_OpenGamepad(id);
            if (pad == null)
                return;
            device = new SdlDevice(id, pad, SDL_GetGamepadJoystick(pad), SDL_GetGamepadName(pad) ?? "Gamepad");
        }
        else
        {
            var joystick = SDL_OpenJoystick(id);
            if (joystick == null)
                return;
            device = new SdlDevice(id, null, joystick, SDL_GetJoystickName(joystick) ?? "Joystick");
        }
        _devices.Add(device);
        if (announce)
            Message?.Invoke($"Player {_devices.Count} controller connected: {device.Name}");
    }

    public void RemoveDevice(SDL_JoystickID id)
    {
        var index = _devices.FindIndex(d => d.Id == id);
        if (index < 0)
            return;
        _devices[index].Close();
        _devices.RemoveAt(index);
        Message?.Invoke($"Player {index + 1} controller disconnected");
    }

    // ---- To the core ----

    public void Poll() => Mapper.PollGame(this);

    public short GetState(uint port, uint device, uint index, uint id)
    {
        if (port >= ArcadeControls.MaxPlayers)
            return 0;
        return device switch
        {
            RetroDevice.Joypad when id < 16 => (short)((Mapper.RetroButtons((int)port) >> (int)id) & 1),
            RetroDevice.Analog when index == 0 && id < 2 && port < _devices.Count => id == 0 ? _devices[(int)port].Stick.X : _devices[(int)port].Stick.Y,
            _ => 0,
        };
    }

    // ---- Binding capture ("press the button for…") ----

    readonly HashSet<(int Device, Binding Binding)> _captureBaseline = new();
    readonly List<(int Device, Binding Binding)> _active = new();

    /// <summary>Starts listening for a new press; anything already held is ignored until released.</summary>
    public void BeginCapture()
    {
        _captureBaseline.Clear();
        CollectActive();
        _captureBaseline.UnionWith(_active);
        foreach (var device in _devices)
            device.RememberRestingAxes();
    }

    /// <summary>
    /// Returns the first key or button pressed since <see cref="BeginCapture"/>, with the index of the
    /// device it came from (-1 for the keyboard).
    /// </summary>
    public bool TryCapture(out Binding binding, out int device)
    {
        CollectActive();
        _captureBaseline.IntersectWith(_active); // released inputs can be captured when pressed again
        foreach (var item in _active)
        {
            if (_captureBaseline.Contains(item))
                continue;
            (device, binding) = item;
            return true;
        }
        binding = default;
        device = -1;
        return false;
    }

    void CollectActive()
    {
        _active.Clear();
        for (var sc = 1; sc < (int)SDL_Scancode.SDL_SCANCODE_COUNT; sc++)
            if (_keyboard[sc])
                _active.Add((-1, Binding.Key((SDL_Scancode)sc)));
        for (var i = 0; i < _devices.Count; i++)
            _devices[i].CollectActive(i, _active);
    }

    public void Dispose()
    {
        foreach (var device in _devices)
            device.Close();
        _devices.Clear();
    }

    /// <summary>One open SDL device: a gamepad (with its joystick underneath) or a plain joystick.</summary>
    sealed class SdlDevice(SDL_JoystickID id, SDL_Gamepad* pad, SDL_Joystick* joystick, string name) : IRawDevice
    {
        short[] _restingAxes = [];

        public SDL_JoystickID Id => id;
        public SDL_Gamepad* Pad => pad;
        public string Name => name;
        public bool IsGamepad => pad != null;

        public bool IsDown(Binding b) => b.Kind switch
        {
            BindingKind.PadButton => pad != null && SDL_GetGamepadButton(pad, (SDL_GamepadButton)b.Code),
            BindingKind.PadAxis => pad != null && SDL_GetGamepadAxis(pad, (SDL_GamepadAxis)b.Code) * b.Direction > AxisThreshold,
            BindingKind.JoyButton => joystick != null && SDL_GetJoystickButton(joystick, b.Code),
            BindingKind.JoyHat => joystick != null && (SDL_GetJoystickHat(joystick, b.Code) & b.Direction) != 0,
            BindingKind.JoyAxis => joystick != null && SDL_GetJoystickAxis(joystick, b.Code) * b.Direction > AxisThreshold,
            _ => false,
        };

        public (short X, short Y) Stick => pad != null
            ? (SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX), SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY))
            : joystick != null && SDL_GetNumJoystickAxes(joystick) >= 2
                ? (SDL_GetJoystickAxis(joystick, 0), SDL_GetJoystickAxis(joystick, 1))
                : ((short)0, (short)0);

        /// <summary>Some joystick axes rest at one end (e.g. pedals, DirectInput triggers); capture measures from where they rest.</summary>
        public void RememberRestingAxes()
        {
            if (pad != null || joystick == null)
                return;
            _restingAxes = new short[Math.Max(0, SDL_GetNumJoystickAxes(joystick))];
            for (var a = 0; a < _restingAxes.Length; a++)
                _restingAxes[a] = SDL_GetJoystickAxis(joystick, a);
        }

        public void CollectActive(int index, List<(int, Binding)> active)
        {
            if (pad != null)
            {
                for (var b = 0; b < (int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_COUNT; b++)
                    if (SDL_GetGamepadButton(pad, (SDL_GamepadButton)b))
                        active.Add((index, Binding.Pad((SDL_GamepadButton)b)));
                for (var a = 0; a < (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_COUNT; a++)
                {
                    var value = SDL_GetGamepadAxis(pad, (SDL_GamepadAxis)a);
                    if (Math.Abs((int)value) > CaptureThreshold)
                        active.Add((index, Binding.PadAxis((SDL_GamepadAxis)a, value)));
                }
                return;
            }
            if (joystick == null)
                return;
            for (var b = 0; b < SDL_GetNumJoystickButtons(joystick); b++)
                if (SDL_GetJoystickButton(joystick, b))
                    active.Add((index, new Binding(BindingKind.JoyButton, b)));
            for (var h = 0; h < SDL_GetNumJoystickHats(joystick); h++)
            {
                var hat = SDL_GetJoystickHat(joystick, h);
                foreach (var bit in (ReadOnlySpan<int>)[Binding.HatUp, Binding.HatRight, Binding.HatDown, Binding.HatLeft])
                    if ((hat & bit) != 0)
                        active.Add((index, new Binding(BindingKind.JoyHat, h, bit)));
            }
            for (var a = 0; a < SDL_GetNumJoystickAxes(joystick); a++)
            {
                var value = SDL_GetJoystickAxis(joystick, a);
                var rest = a < _restingAxes.Length ? _restingAxes[a] : (short)0;
                if (Math.Abs(value - rest) > CaptureThreshold && Math.Abs((int)value) > AxisThreshold)
                    active.Add((index, new Binding(BindingKind.JoyAxis, a, Math.Sign(value))));
            }
        }

        public void Close()
        {
            if (pad != null)
                SDL_CloseGamepad(pad);
            else if (joystick != null)
                SDL_CloseJoystick(joystick);
        }
    }
}
