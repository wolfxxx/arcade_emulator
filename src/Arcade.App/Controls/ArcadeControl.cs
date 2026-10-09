using Arcade.Libretro;

namespace Arcade.App.Controls;

/// <summary>The controls of one player's place on an arcade panel: a stick, up to eight buttons, coin and start.</summary>
enum ArcadeControl
{
    Up, Down, Left, Right,
    Button1, Button2, Button3, Button4, Button5, Button6, Button7, Button8,
    Coin, Start,
}

/// <summary>Things the app does (rather than the game) when a key or button is pressed during play.</summary>
enum Hotkey
{
    Menu, SaveState, LoadState, Reset, Screenshot, ExitGame,
    /// <summary>Held: the game runs backwards.</summary>
    Rewind,
    /// <summary>Held: the game runs faster.</summary>
    FastForward,
    /// <summary>Pressed: slow motion on or off.</summary>
    SlowMotion,
}

static class ArcadeControls
{
    public const int Count = 14;
    public const int MaxPlayers = 4;
    public const int ButtonCount = 8;

    public static readonly ArcadeControl[] All = Enum.GetValues<ArcadeControl>();
    public static readonly ArcadeControl[] Directions = [ArcadeControl.Up, ArcadeControl.Down, ArcadeControl.Left, ArcadeControl.Right];

    public static ArcadeControl Button(int number) => ArcadeControl.Button1 + (number - 1);

    /// <summary>1-based button number, or 0 for the stick, coin and start.</summary>
    public static int ButtonNumber(this ArcadeControl control) =>
        control is >= ArcadeControl.Button1 and <= ArcadeControl.Button8 ? control - ArcadeControl.Button1 + 1 : 0;

    /// <summary>
    /// The RetroPad button each panel control drives. Buttons follow libretro's arcade convention
    /// (B, A, Y, X, L, R, L2, R2), which FBNeo and MAME 2003-Plus both use as buttons 1–8.
    /// </summary>
    public static JoypadButton RetroButton(this ArcadeControl control) => control switch
    {
        ArcadeControl.Up => JoypadButton.Up,
        ArcadeControl.Down => JoypadButton.Down,
        ArcadeControl.Left => JoypadButton.Left,
        ArcadeControl.Right => JoypadButton.Right,
        ArcadeControl.Button1 => JoypadButton.B,
        ArcadeControl.Button2 => JoypadButton.A,
        ArcadeControl.Button3 => JoypadButton.Y,
        ArcadeControl.Button4 => JoypadButton.X,
        ArcadeControl.Button5 => JoypadButton.L,
        ArcadeControl.Button6 => JoypadButton.R,
        ArcadeControl.Button7 => JoypadButton.L2,
        ArcadeControl.Button8 => JoypadButton.R2,
        ArcadeControl.Coin => JoypadButton.Select,
        ArcadeControl.Start => JoypadButton.Start,
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    public static string DisplayName(this ArcadeControl control) => control switch
    {
        ArcadeControl.Coin => "Insert coin",
        ArcadeControl.Start => "Start",
        _ when control.ButtonNumber() is var n and > 0 => $"Button {n}",
        _ => control.ToString(),
    };

    public static string DisplayName(this Hotkey hotkey) => hotkey switch
    {
        Hotkey.Menu => "Pause menu",
        Hotkey.SaveState => "Save state",
        Hotkey.LoadState => "Load state",
        Hotkey.Reset => "Reset game",
        Hotkey.Screenshot => "Screenshot",
        Hotkey.ExitGame => "Back to game list",
        Hotkey.Rewind => "Rewind (hold)",
        Hotkey.FastForward => "Fast-forward (hold)",
        Hotkey.SlowMotion => "Slow motion on/off",
        _ => hotkey.ToString(),
    };

    /// <summary>
    /// Turns a direction pushed on the panel into the game's direction when the picture is shown
    /// turned <paramref name="clockwiseTurns"/> quarter turns clockwise, so "up" on the stick is
    /// still "up" on screen.
    /// </summary>
    public static ArcadeControl RotateDirection(ArcadeControl direction, int clockwiseTurns)
    {
        // Clockwise order on screen; a picture turned clockwise means screen directions map back anticlockwise.
        ReadOnlySpan<ArcadeControl> ring = [ArcadeControl.Up, ArcadeControl.Right, ArcadeControl.Down, ArcadeControl.Left];
        var i = direction switch { ArcadeControl.Up => 0, ArcadeControl.Right => 1, ArcadeControl.Down => 2, ArcadeControl.Left => 3, _ => -1 };
        if (i < 0)
            return direction;
        return ring[((i - clockwiseTurns) % 4 + 4) % 4];
    }
}
