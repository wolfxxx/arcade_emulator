namespace Arcade.Libretro;

/// <summary>Supplies controller state to the core. Called on the emulation thread inside retro_run.</summary>
public interface IInputSource
{
    /// <summary>Called once per frame before the core reads input.</summary>
    void Poll();

    /// <summary>Returns the state of one control; for joypad buttons non-zero means pressed.</summary>
    short GetState(uint port, uint device, uint index, uint id);
}

/// <summary>Receives interleaved stereo 16-bit samples at the core's sample rate.</summary>
public interface IAudioSink
{
    void Write(ReadOnlySpan<short> interleavedStereo);
}

public sealed record CoreHostOptions
{
    public required string SystemDirectory { get; init; }
    public required string SaveDirectory { get; init; }
    public IInputSource? Input { get; init; }
    public IAudioSink? Audio { get; init; }
    public Action<LogLevel, string>? Log { get; init; }
    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Info;

    /// <summary>Core option values to apply instead of the core's defaults, by option key.</summary>
    public IReadOnlyDictionary<string, string> OptionOverrides { get; init; } = new Dictionary<string, string>();
}

/// <summary>What a control does in the loaded game, as the core describes it (SET_INPUT_DESCRIPTORS), e.g. "Weak Punch".</summary>
public sealed record InputDescriptor(uint Port, uint Device, uint Index, uint Id, string Description);

public sealed record CoreInfo(string Name, string Version, string ValidExtensions, bool NeedFullPath, bool BlockExtract);
