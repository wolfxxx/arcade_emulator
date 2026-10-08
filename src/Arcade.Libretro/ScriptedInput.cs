namespace Arcade.Libretro;

/// <summary>
/// Plays back a fixed list of button presses by frame number. Used for headless runs and tests
/// (e.g. insert coin, press start) so results are reproducible.
/// </summary>
public sealed class ScriptedInput : IInputSource
{
    readonly List<(long From, long To, uint Port, JoypadButton Button)> _presses = new();
    readonly Func<long> _frame;

    public ScriptedInput(Func<long> currentFrame) => _frame = currentFrame;

    /// <summary>Holds <paramref name="button"/> on <paramref name="port"/> for frames [from, from + frames).</summary>
    public ScriptedInput Press(long from, JoypadButton button, long frames = 6, uint port = 0)
    {
        _presses.Add((from, from + frames, port, button));
        return this;
    }

    public void Poll() { }

    public short GetState(uint port, uint device, uint index, uint id)
    {
        if (device != RetroDevice.Joypad)
            return 0;
        var frame = _frame();
        foreach (var p in _presses)
            if (p.Port == port && (uint)p.Button == id && frame >= p.From && frame < p.To)
                return 1;
        return 0;
    }
}
