using Arcade.App.Controls;
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

    // Keys that work in menus whatever the game controls are. Everything else comes from player 1's
    // controls and every pad (see ActionControls), so a cabinet's own buttons drive the menus.
    static readonly (SDL_Scancode Key, UiAction Action)[] Keys =
    [
        (SDL_Scancode.SDL_SCANCODE_UP, UiAction.Up), (SDL_Scancode.SDL_SCANCODE_DOWN, UiAction.Down),
        (SDL_Scancode.SDL_SCANCODE_LEFT, UiAction.Left), (SDL_Scancode.SDL_SCANCODE_RIGHT, UiAction.Right),
        (SDL_Scancode.SDL_SCANCODE_RETURN, UiAction.Accept), (SDL_Scancode.SDL_SCANCODE_KP_ENTER, UiAction.Accept),
        (SDL_Scancode.SDL_SCANCODE_ESCAPE, UiAction.Back), (SDL_Scancode.SDL_SCANCODE_BACKSPACE, UiAction.Back),
        (SDL_Scancode.SDL_SCANCODE_TAB, UiAction.Options),
        (SDL_Scancode.SDL_SCANCODE_F, UiAction.Favorite),
        (SDL_Scancode.SDL_SCANCODE_PAGEUP, UiAction.PageUp), (SDL_Scancode.SDL_SCANCODE_PAGEDOWN, UiAction.PageDown),
        (SDL_Scancode.SDL_SCANCODE_HOME, UiAction.Home), (SDL_Scancode.SDL_SCANCODE_END, UiAction.End),
        (SDL_Scancode.SDL_SCANCODE_SLASH, UiAction.Search), (SDL_Scancode.SDL_SCANCODE_F3, UiAction.Search),
    ];

    /// <summary>Which panel controls perform each menu action (the first one is shown in on-screen hints).</summary>
    public static readonly (UiAction Action, ArcadeControl[] Controls)[] ActionControls =
    [
        (UiAction.Up, [ArcadeControl.Up]), (UiAction.Down, [ArcadeControl.Down]),
        (UiAction.Left, [ArcadeControl.Left]), (UiAction.Right, [ArcadeControl.Right]),
        (UiAction.Accept, [ArcadeControl.Button1, ArcadeControl.Start]),
        (UiAction.Back, [ArcadeControl.Button2]),
        (UiAction.Favorite, [ArcadeControl.Button3]),
        (UiAction.Options, [ArcadeControl.Button4, ArcadeControl.Coin]),
        (UiAction.PrevTab, [ArcadeControl.Button5]), (UiAction.NextTab, [ArcadeControl.Button6]),
        (UiAction.PageUp, [ArcadeControl.Button7]), (UiAction.PageDown, [ArcadeControl.Button8]),
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

    /// <summary>How long an action's button has been held down (0 when released).</summary>
    public float HeldFor(UiAction action) => _held[(int)action] ? _heldFor[(int)action] : 0;

    /// <param name="mapper">Game controls, already read for this frame.</param>
    public void Update(float dt, ControlMapper mapper, bool keyboardEnabled = true)
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

        foreach (var (action, controls) in ActionControls)
            foreach (var control in controls)
                if (mapper.AnyHeld(control, out var fromDevice))
                    Press(now, action, fromDevice ? InputDevice.Gamepad : InputDevice.Keyboard);

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

    /// <summary>
    /// Finds the on-screen name of the key (keyboard = true) or pad button bound to a control, so
    /// hints follow the player's own layout. Set by the app.
    /// </summary>
    public Func<ArcadeControl, bool, string?>? BindingLabel { get; set; }

    /// <summary>On-screen name of the key or button for an action, matching the last device used.</summary>
    public string Label(UiAction action)
    {
        var keyboard = LastDevice == InputDevice.Keyboard;
        // Fixed keys first on the keyboard (Enter, Esc, Tab…), since they're what people expect to read.
        var fixedKey = action switch
        {
            UiAction.Accept => "Enter",
            UiAction.Back => "Esc",
            UiAction.Options => "Tab",
            UiAction.Favorite => "F",
            UiAction.Search => "/",
            UiAction.PageUp or UiAction.PageDown => "PgUp/PgDn",
            _ => null,
        };
        if (keyboard && fixedKey != null)
            return fixedKey;
        foreach (var (a, controls) in ActionControls)
            if (a == action && BindingLabel?.Invoke(controls[0], keyboard) is { } label)
                return label;
        return fixedKey ?? action.ToString();
    }
}