using SDL;

namespace Arcade.App.Controls;

enum BindingKind
{
    /// <summary>A keyboard key (SDL scancode); arcade encoders such as the I-PAC also send keys.</summary>
    Key,
    /// <summary>A button on a recognised gamepad (Xbox, PlayStation, Switch… layouts).</summary>
    PadButton,
    /// <summary>A gamepad stick or trigger pushed one way past the halfway point.</summary>
    PadAxis,
    /// <summary>A button on a joystick SDL doesn't know as a gamepad, e.g. a USB arcade encoder.</summary>
    JoyButton,
    /// <summary>One direction of a joystick hat (often how encoders report the stick).</summary>
    JoyHat,
    /// <summary>A joystick axis pushed one way.</summary>
    JoyAxis,
}

/// <summary>
/// One physical input: a key, a pad button, a stick direction… Written in controls.json as short
/// text: <c>key:z</c>, <c>pad:south</c>, <c>pad:leftx-</c>, <c>joy:button3</c>, <c>joy:hat0up</c>, <c>joy:axis1+</c>.
/// </summary>
/// <param name="Code">Scancode, button, axis or hat number.</param>
/// <param name="Direction">Axis sign (-1/+1) or hat direction bit (1 up, 2 right, 4 down, 8 left).</param>
readonly record struct Binding(BindingKind Kind, int Code, int Direction = 0)
{
    public const int HatUp = 1, HatRight = 2, HatDown = 4, HatLeft = 8;

    public bool IsKeyboard => Kind == BindingKind.Key;

    public static Binding Key(SDL_Scancode key) => new(BindingKind.Key, (int)key);
    public static Binding Pad(SDL_GamepadButton button) => new(BindingKind.PadButton, (int)button);
    public static Binding PadAxis(SDL_GamepadAxis axis, int sign) => new(BindingKind.PadAxis, (int)axis, Math.Sign(sign));

    public override string ToString() => Kind switch
    {
        BindingKind.Key => "key:" + EnumName(((SDL_Scancode)Code).ToString(), "SDL_SCANCODE_"),
        BindingKind.PadButton => "pad:" + EnumName(((SDL_GamepadButton)Code).ToString(), "SDL_GAMEPAD_BUTTON_"),
        BindingKind.PadAxis => "pad:" + EnumName(((SDL_GamepadAxis)Code).ToString(), "SDL_GAMEPAD_AXIS_") + Sign,
        BindingKind.JoyButton => $"joy:button{Code}",
        BindingKind.JoyHat => $"joy:hat{Code}{HatName(Direction)}",
        BindingKind.JoyAxis => $"joy:axis{Code}{Sign}",
        _ => "?",
    };

    string Sign => Direction < 0 ? "-" : "+";

    static string EnumName(string name, string prefix) =>
        (name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name).ToLowerInvariant();

    static string HatName(int bit) => bit switch { HatUp => "up", HatRight => "right", HatDown => "down", HatLeft => "left", _ => "?" };

    public static Binding Parse(string text) =>
        TryParse(text, out var binding) ? binding : throw new FormatException($"'{text}' is not a key or button (e.g. key:z, pad:south, joy:button3).");

    public static bool TryParse(string? text, out Binding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var colon = text.IndexOf(':');
        if (colon <= 0)
            return false;
        var kind = text[..colon].Trim().ToLowerInvariant();
        var name = text[(colon + 1)..].Trim().ToLowerInvariant();
        switch (kind)
        {
            case "key" when TryEnum<SDL_Scancode>("SDL_SCANCODE_", name, out var key) && key != SDL_Scancode.SDL_SCANCODE_UNKNOWN:
                binding = Key(key);
                return true;
            case "pad" when TryEnum<SDL_GamepadButton>("SDL_GAMEPAD_BUTTON_", name, out var button) && button >= 0 && button < SDL_GamepadButton.SDL_GAMEPAD_BUTTON_COUNT:
                binding = Pad(button);
                return true;
            case "pad" when name.Length > 1 && name[^1] is '+' or '-'
                            && TryEnum<SDL_GamepadAxis>("SDL_GAMEPAD_AXIS_", name[..^1], out var axis) && axis >= 0 && axis < SDL_GamepadAxis.SDL_GAMEPAD_AXIS_COUNT:
                binding = PadAxis(axis, name[^1] == '-' ? -1 : 1);
                return true;
            case "joy" when name.StartsWith("button") && int.TryParse(name.AsSpan(6), out var b) && b >= 0:
                binding = new Binding(BindingKind.JoyButton, b);
                return true;
            case "joy" when name.StartsWith("axis") && name.Length > 5 && name[^1] is '+' or '-' && int.TryParse(name.AsSpan(4, name.Length - 5), out var a) && a >= 0:
                binding = new Binding(BindingKind.JoyAxis, a, name[^1] == '-' ? -1 : 1);
                return true;
            case "joy" when name.StartsWith("hat"):
            {
                var digits = name.AsSpan(3);
                var end = 0;
                while (end < digits.Length && char.IsAsciiDigit(digits[end])) end++;
                if (end == 0 || !int.TryParse(digits[..end], out var hat))
                    return false;
                var dir = digits[end..].ToString() switch { "up" => HatUp, "right" => HatRight, "down" => HatDown, "left" => HatLeft, _ => 0 };
                if (dir == 0)
                    return false;
                binding = new Binding(BindingKind.JoyHat, hat, dir);
                return true;
            }
        }
        return false;
    }

    static bool TryEnum<T>(string prefix, string name, out T value) where T : struct, Enum =>
        Enum.TryParse(prefix + name.ToUpperInvariant(), ignoreCase: false, out value) && Enum.IsDefined(value);

    /// <summary>Short on-screen name, e.g. "Z", "Pad Ⓐ", "Stick ←", "Joy 3".</summary>
    public string Label => Kind switch
    {
        BindingKind.Key => KeyLabel((SDL_Scancode)Code),
        BindingKind.PadButton => "Pad " + PadButtonLabel((SDL_GamepadButton)Code),
        BindingKind.PadAxis => (SDL_GamepadAxis)Code switch
        {
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX => Direction < 0 ? "Stick ←" : "Stick →",
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY => Direction < 0 ? "Stick ↑" : "Stick ↓",
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX => Direction < 0 ? "R-stick ←" : "R-stick →",
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY => Direction < 0 ? "R-stick ↑" : "R-stick ↓",
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER => "Pad LT",
            SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER => "Pad RT",
            _ => ToString(),
        },
        BindingKind.JoyButton => $"Joy {Code + 1}",
        BindingKind.JoyHat => "Joy " + Direction switch { HatUp => "↑", HatRight => "→", HatDown => "↓", _ => "←" } + (Code > 0 ? $" (hat {Code + 1})" : ""),
        BindingKind.JoyAxis => $"Joy axis {Code + 1}{Sign}",
        _ => ToString(),
    };

    static string PadButtonLabel(SDL_GamepadButton button) => button switch
    {
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => "Ⓐ",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST => "Ⓑ",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST => "Ⓧ",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH => "Ⓨ",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK => "Back",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE => "Guide",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START => "Start",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK => "L3",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK => "R3",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER => "LB",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER => "RB",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => "↑",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => "↓",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => "←",
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => "→",
        _ => EnumName(button.ToString(), "SDL_GAMEPAD_BUTTON_"),
    };

    static string KeyLabel(SDL_Scancode key) => key switch
    {
        SDL_Scancode.SDL_SCANCODE_UP => "Arrow ↑",
        SDL_Scancode.SDL_SCANCODE_DOWN => "Arrow ↓",
        SDL_Scancode.SDL_SCANCODE_LEFT => "Arrow ←",
        SDL_Scancode.SDL_SCANCODE_RIGHT => "Arrow →",
        SDL_Scancode.SDL_SCANCODE_LCTRL => "L-Ctrl",
        SDL_Scancode.SDL_SCANCODE_RCTRL => "R-Ctrl",
        SDL_Scancode.SDL_SCANCODE_LALT => "L-Alt",
        SDL_Scancode.SDL_SCANCODE_RALT => "R-Alt",
        SDL_Scancode.SDL_SCANCODE_LSHIFT => "L-Shift",
        SDL_Scancode.SDL_SCANCODE_RSHIFT => "R-Shift",
        SDL_Scancode.SDL_SCANCODE_RETURN => "Enter",
        SDL_Scancode.SDL_SCANCODE_ESCAPE => "Esc",
        SDL_Scancode.SDL_SCANCODE_SPACE => "Space",
        SDL_Scancode.SDL_SCANCODE_BACKSPACE => "Backspace",
        SDL_Scancode.SDL_SCANCODE_PAUSE => "Pause",
        _ => EnumName(key.ToString(), "SDL_SCANCODE_") switch
        {
            [var c] => char.ToUpperInvariant(c).ToString(),
            var s when s.StartsWith("kp_") => "Keypad " + s[3..].ToUpperInvariant(),
            var s when s.Length <= 3 => s.ToUpperInvariant(), // F1…F12
            var s => char.ToUpperInvariant(s[0]) + s[1..],
        },
    };
}
