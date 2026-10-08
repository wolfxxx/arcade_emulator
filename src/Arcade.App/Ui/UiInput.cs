using SDL;
using static SDL.SDL3;

namespace Arcade.App.Ui;

enum UiAction
{
    Up, Down, Left, Right,
    Accept, Back, Options, Favorite,
    PageUp, PageDown, Home, End,
    PrevTab, NextTab, Search,
}

/// <summary>Where the last menu input came from, so on-screen hints can show matching button names.</summary>
enum InputDevice { Keyboard, Gamepad }

/// <summary>
/// Turns keyboard and gamepad state into menu actions. A held direction repeats after a short
/// delay and speeds up the longer it is held, so long lists can be scrolled with a stick alone.
/// </summary>
sealed unsafe class UiInput
{
    const float RepeatDelay = 0.35f;
    const float RepeatInterval = 0.075f;
    const float FastInterval = 0.03f;
    const float FastAfter = 1.6f;
    const short StickThreshold = 20000;

    static readonly (SDL_Scancode Key, UiAction Action)[] Keys =
    [
        (SDL_Scancode.SDL_SCANCODE_UP, UiAction.Up), (SDL_Scancode.SDL_SCANCODE_DOWN, UiAction.Down),
        (SDL_Scancode.SDL_SCANCODE_LEFT, UiAction.Left), (SDL_Scancode.SDL_SCANCODE_RIGHT, UiAction.Right),
        (SDL_Scancode.SDL_SCANCODE_RETURN, UiAction.Accept), (SDL_Scancode.SDL_SCANCODE_KP_ENTER, UiAction.Accept),
        (SDL_Scancode.SDL_SCANCODE_Z, UiAction.Accept), (SDL_Scancode.SDL_SCANCODE_LCTRL, UiAction.Accept),
        (SDL_Scancode.SDL_SCANCODE_1, UiAction.Accept), (SDL_Scancode.SDL_SCANCODE_SPACE, UiAction.Accept),
        (SDL_Scancode.SDL_SCANCODE_ESCAPE, UiAction.Back), (SDL_Scancode.SDL_SCANCODE_BACKSPACE, UiAction.Back),
        (SDL_Scancode.SDL_SCANCODE_X, UiAction.Back), (SDL_Scancode.SDL_SCANCODE_LALT, UiAction.Back),
        (SDL_Scancode.SDL_SCANCODE_TAB, UiAction.Options), (SDL_Scancode.SDL_SCANCODE_S, UiAction.Options),
        (SDL_Scancode.SDL_SCANCODE_LSHIFT, UiAction.Options),
        (SDL_Scancode.SDL_SCANCODE_F, UiAction.Favorite), (SDL_Scancode.SDL_SCANCODE_A, UiAction.Favorite),
        (SDL_Scancode.SDL_SCANCODE_PAGEUP, UiAction.PageUp), (SDL_Scancode.SDL_SCANCODE_PAGEDOWN, UiAction.PageDown),
        (SDL_Scancode.SDL_SCANCODE_HOME, UiAction.Home), (SDL_Scancode.SDL_SCANCODE_END, UiAction.End),
        (SDL_Scancode.SDL_SCANCODE_Q, UiAction.PrevTab), (SDL_Scancode.SDL_SCANCODE_W, UiAction.NextTab),
        (SDL_Scancode.SDL_SCANCODE_SLASH, UiAction.Search), (SDL_Scancode.SDL_SCANCODE_F3, UiAction.Search),
    ];

    static readonly (SDL_GamepadButton Button, UiAction Action)[] PadButtons =
    [
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, UiAction.Up), (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, UiAction.Down),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, UiAction.Left), (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, UiAction.Right),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, UiAction.Accept), (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, UiAction.Accept),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, UiAction.Back),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH, UiAction.Options), (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, UiAction.Options),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, UiAction.Favorite),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, UiAction.PrevTab), (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, UiAction.NextTab),
    ];

    // Only movement repeats; repeating Accept or Back would open and close things by accident.
    static readonly HashSet<UiAction> Repeating = [UiAction.Up, UiAction.Down, UiAction.Left, UiAction.Right, UiAction.PageUp, UiAction.PageDown];

    static readonly int ActionCount = Enum.GetValues<UiAction>().Length;
    readonly float[] _heldFor = new float[ActionCount];
    readonly float[] _nextRepeat = new float[ActionCount];
    readonly bool[] _held = new bool[ActionCount];
    readonly bool[] _injected = new bool[ActionCount];
    readonly List<UiAction> _actions = new();
    bool _suppressUntilReleased;

    /// <summary>Actions that fired this frame, in order.</summary>
    public IReadOnlyList<UiAction> Actions => _actions;

    public InputDevice LastDevice { get; private set; } = InputDevice.Keyboard;

    /// <summary>Seconds since any menu input; drives the attract mode.</summary>
    public float IdleSeconds { get; private set; }

    /// <summary>
    /// Ignores everything currently held until it is released, e.g. after leaving a game while
    /// buttons are still down, so the press that closed one screen doesn't act on the next.
    /// </summary>
    public void SuppressHeld() => _suppressUntilReleased = true;

    /// <summary>Fires an action as if pressed this frame (scripts, mouse wheel).</summary>
    public void Inject(UiAction action) => _injected[(int)action] = true;

    public void MarkActivity() => IdleSeconds = 0;

    /// <param name="pads">Open gamepads to read; the game input manager owns them.</param>
    public void Update(float dt, IEnumerable<nint> pads, bool keyboardEnabled = true)
    {
        _actions.Clear();
        IdleSeconds += dt;
        Span<bool> now = stackalloc bool[_held.Length];

        if (keyboardEnabled)
        {
            var keyboard = SDL_GetKeyboardState(null);
            foreach (var (key, action) in Keys)
                if (keyboard[(int)key])
                {
                    now[(int)action] = true;
                    if (!_held[(int)action]) LastDevice = InputDevice.Keyboard;
                }
        }

        foreach (var handle in pads)
        {
            var pad = (SDL_Gamepad*)handle;
            foreach (var (button, action) in PadButtons)
                if (SDL_GetGamepadButton(pad, button))
                    Press(now, action, InputDevice.Gamepad);
            var x = SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX);
            var y = SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY);
            if (y < -StickThreshold) Press(now, UiAction.Up, InputDevice.Gamepad);
            if (y > StickThreshold) Press(now, UiAction.Down, InputDevice.Gamepad);
            if (x < -StickThreshold) Press(now, UiAction.Left, InputDevice.Gamepad);
            if (x > StickThreshold) Press(now, UiAction.Right, InputDevice.Gamepad);
            if (SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER) > StickThreshold) Press(now, UiAction.PageUp, InputDevice.Gamepad);
            if (SDL_GetGamepadAxis(pad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER) > StickThreshold) Press(now, UiAction.PageDown, InputDevice.Gamepad);
        }

        if (_suppressUntilReleased)
        {
            var anyHeld = false;
            foreach (var h in now) anyHeld |= h;
            if (anyHeld)
            {
                now.CopyTo(_held);
                Array.Clear(_injected);
                return;
            }
            _suppressUntilReleased = false;
        }

        for (var i = 0; i < _held.Length; i++)
        {
            var action = (UiAction)i;
            if (_injected[i])
            {
                _actions.Add(action);
                _injected[i] = false;
                IdleSeconds = 0;
            }
            if (!now[i])
            {
                _held[i] = false;
                continue;
            }
            IdleSeconds = 0;
            if (!_held[i])
            {
                _held[i] = true;
                _heldFor[i] = 0;
                _nextRepeat[i] = RepeatDelay;
                _actions.Add(action);
                continue;
            }
            _heldFor[i] += dt;
            if (Repeating.Contains(action) && _heldFor[i] >= _nextRepeat[i])
            {
                _actions.Add(action);
                _nextRepeat[i] += _heldFor[i] > FastAfter ? FastInterval : RepeatInterval;
            }
        }
    }

    void Press(Span<bool> now, UiAction action, InputDevice device)
    {
        now[(int)action] = true;
        if (!_held[(int)action])
            LastDevice = device;
    }

    /// <summary>On-screen name of the key or button for an action, matching the last device used.</summary>
    public string Label(UiAction action) => LastDevice == InputDevice.Gamepad
        ? action switch
        {
            UiAction.Accept => "Ⓐ",
            UiAction.Back => "Ⓑ",
            UiAction.Options => "Ⓨ",
            UiAction.Favorite => "Ⓧ",
            UiAction.PrevTab => "LB",
            UiAction.NextTab => "RB",
            UiAction.PageUp or UiAction.PageDown => "LT/RT",
            _ => action.ToString(),
        }
        : action switch
        {
            UiAction.Accept => "Enter",
            UiAction.Back => "Esc",
            UiAction.Options => "Tab",
            UiAction.Favorite => "F",
            UiAction.PrevTab => "Q",
            UiAction.NextTab => "W",
            UiAction.Search => "/",
            UiAction.PageUp or UiAction.PageDown => "PgUp/PgDn",
            _ => action.ToString(),
        };
}
