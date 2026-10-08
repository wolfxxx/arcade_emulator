using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Arcade.Libretro;

/// <summary>
/// Loads a libretro core DLL and drives it. libretro cores keep global state and call back through
/// plain C function pointers, so only one <see cref="CoreHost"/> may exist per process at a time;
/// the static callbacks route to it via <see cref="s_current"/>.
/// </summary>
public sealed unsafe class CoreHost : IDisposable
{
    static CoreHost? s_current;

    readonly nint _library;
    readonly CoreHostOptions _options;
    readonly string _corePath;
    readonly Dictionary<string, nint> _nativeStrings = new();
    readonly HashSet<uint> _reportedUnknownEnv = new();
    readonly Dictionary<string, CoreOption> _coreOptions = new();
    byte* _gameData;
    bool _gameLoaded;
    bool _initialized;
    bool _optionsDirty;

    // Core exports.
    readonly delegate* unmanaged[Cdecl]<void> _retroInit;
    readonly delegate* unmanaged[Cdecl]<void> _retroDeinit;
    readonly delegate* unmanaged[Cdecl]<uint> _retroApiVersion;
    readonly delegate* unmanaged[Cdecl]<RetroSystemInfo*, void> _retroGetSystemInfo;
    readonly delegate* unmanaged[Cdecl]<SystemAvInfo*, void> _retroGetSystemAvInfo;
    readonly delegate* unmanaged[Cdecl]<uint, uint, void> _retroSetControllerPortDevice;
    readonly delegate* unmanaged[Cdecl]<void> _retroReset;
    readonly delegate* unmanaged[Cdecl]<void> _retroRun;
    readonly delegate* unmanaged[Cdecl]<nuint> _retroSerializeSize;
    readonly delegate* unmanaged[Cdecl]<void*, nuint, byte> _retroSerialize;
    readonly delegate* unmanaged[Cdecl]<void*, nuint, byte> _retroUnserialize;
    readonly delegate* unmanaged[Cdecl]<RetroGameInfo*, byte> _retroLoadGame;
    readonly delegate* unmanaged[Cdecl]<void> _retroUnloadGame;

    public CoreInfo Info { get; }
    public SystemAvInfo AvInfo { get; private set; }
    public PixelFormat PixelFormat { get; private set; } = PixelFormat.Rgb1555;

    /// <summary>Number of 90° counter-clockwise turns the core asked for (SET_ROTATION).</summary>
    public uint Rotation { get; private set; }

    public VideoFrame LastFrame { get; } = new();
    public long FrameCount { get; private set; }
    public long AudioFramesReceived { get; private set; }
    public bool ShutdownRequested { get; private set; }
    public IReadOnlyDictionary<string, CoreOption> Options => _coreOptions;

    CoreHost(string corePath, CoreHostOptions options)
    {
        _corePath = Path.GetFullPath(corePath);
        _options = options;
        _library = NativeLibrary.Load(_corePath);

        _retroInit = (delegate* unmanaged[Cdecl]<void>)Export("retro_init");
        _retroDeinit = (delegate* unmanaged[Cdecl]<void>)Export("retro_deinit");
        _retroApiVersion = (delegate* unmanaged[Cdecl]<uint>)Export("retro_api_version");
        _retroGetSystemInfo = (delegate* unmanaged[Cdecl]<RetroSystemInfo*, void>)Export("retro_get_system_info");
        _retroGetSystemAvInfo = (delegate* unmanaged[Cdecl]<SystemAvInfo*, void>)Export("retro_get_system_av_info");
        _retroSetControllerPortDevice = (delegate* unmanaged[Cdecl]<uint, uint, void>)Export("retro_set_controller_port_device");
        _retroReset = (delegate* unmanaged[Cdecl]<void>)Export("retro_reset");
        _retroRun = (delegate* unmanaged[Cdecl]<void>)Export("retro_run");
        _retroSerializeSize = (delegate* unmanaged[Cdecl]<nuint>)Export("retro_serialize_size");
        _retroSerialize = (delegate* unmanaged[Cdecl]<void*, nuint, byte>)Export("retro_serialize");
        _retroUnserialize = (delegate* unmanaged[Cdecl]<void*, nuint, byte>)Export("retro_unserialize");
        _retroLoadGame = (delegate* unmanaged[Cdecl]<RetroGameInfo*, byte>)Export("retro_load_game");
        _retroUnloadGame = (delegate* unmanaged[Cdecl]<void>)Export("retro_unload_game");

        var api = _retroApiVersion();
        if (api != RetroConst.ApiVersion)
            throw new InvalidOperationException($"{Path.GetFileName(_corePath)} uses libretro API {api}, expected {RetroConst.ApiVersion}.");

        RetroSystemInfo info;
        _retroGetSystemInfo(&info);
        Info = new CoreInfo(
            Utf8(info.LibraryName),
            Utf8(info.LibraryVersion),
            Utf8(info.ValidExtensions),
            info.NeedFullpath != 0,
            info.BlockExtract != 0);
    }

    /// <summary>Loads the core DLL and initialises it. Throws if another core is already active.</summary>
    public static CoreHost Load(string corePath, CoreHostOptions options)
    {
        if (s_current != null)
            throw new InvalidOperationException("A libretro core is already loaded in this process.");

        Directory.CreateDirectory(options.SystemDirectory);
        Directory.CreateDirectory(options.SaveDirectory);

        var host = new CoreHost(corePath, options);
        s_current = host;
        try
        {
            host.Initialize();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    void Initialize()
    {
        ((delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<uint, void*, byte>, void>)Export("retro_set_environment"))(&OnEnvironment);
        ((delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<void*, uint, uint, nuint, void>, void>)Export("retro_set_video_refresh"))(&OnVideoRefresh);
        ((delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<short, short, void>, void>)Export("retro_set_audio_sample"))(&OnAudioSample);
        ((delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<short*, nuint, nuint>, void>)Export("retro_set_audio_sample_batch"))(&OnAudioSampleBatch);
        ((delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<void>, void>)Export("retro_set_input_poll"))(&OnInputPoll);
        ((delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<uint, uint, uint, uint, short>, void>)Export("retro_set_input_state"))(&OnInputState);
        _retroInit();
        _initialized = true;
    }

    /// <summary>Loads a ROM. Arcade cores usually want the zip path rather than its bytes.</summary>
    public void LoadGame(string romPath)
    {
        if (_gameLoaded)
            throw new InvalidOperationException("A game is already loaded.");

        var fullPath = Path.GetFullPath(romPath);
        var game = new RetroGameInfo { Path = NativeString(fullPath) };
        if (!Info.NeedFullPath)
        {
            var bytes = File.ReadAllBytes(fullPath);
            _gameData = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>(_gameData, bytes.Length));
            game.Data = _gameData;
            game.Size = (nuint)bytes.Length;
        }

        if (_retroLoadGame(&game) == 0)
            throw new InvalidOperationException(
                $"{Info.Name} could not load '{Path.GetFileName(fullPath)}'. The ROM set may be the wrong version for this core, incomplete, or need a BIOS in {_options.SystemDirectory}.");
        _gameLoaded = true;

        SystemAvInfo av;
        _retroGetSystemAvInfo(&av);
        AvInfo = av;
        _retroSetControllerPortDevice(0, RetroDevice.Joypad);
        _retroSetControllerPortDevice(1, RetroDevice.Joypad);
    }

    /// <summary>Emulates exactly one frame.</summary>
    public void RunFrame()
    {
        _retroRun();
        FrameCount++;
    }

    public void Reset() => _retroReset();

    public byte[] SaveState()
    {
        var size = _retroSerializeSize();
        if (size == 0)
            throw new NotSupportedException($"{Info.Name} does not support save states for this game.");
        var buffer = new byte[(int)size];
        fixed (byte* p = buffer)
        {
            if (_retroSerialize(p, size) == 0)
                throw new InvalidOperationException("retro_serialize failed.");
        }
        return buffer;
    }

    public void LoadState(ReadOnlySpan<byte> state)
    {
        fixed (byte* p = state)
        {
            if (_retroUnserialize(p, (nuint)state.Length) == 0)
                throw new InvalidOperationException("retro_unserialize failed.");
        }
    }

    /// <summary>Changes a core option; the core picks it up on its next GET_VARIABLE_UPDATE poll.</summary>
    public void SetOption(string key, string value)
    {
        if (!_coreOptions.TryGetValue(key, out var option))
            throw new KeyNotFoundException($"Unknown core option '{key}'.");
        if (!option.Values.Contains(value))
            throw new ArgumentException($"'{value}' is not a valid value for '{key}'. Valid: {string.Join(", ", option.Values)}");
        option.Value = value;
        _optionsDirty = true;
    }

    public void Dispose()
    {
        if (_gameLoaded)
        {
            _retroUnloadGame();
            _gameLoaded = false;
        }
        if (_initialized)
        {
            _retroDeinit();
            _initialized = false;
        }
        NativeLibrary.Free(_library);

        foreach (var p in _nativeStrings.Values)
            Marshal.FreeCoTaskMem(p);
        _nativeStrings.Clear();
        if (_gameData != null)
        {
            NativeMemory.Free(_gameData);
            _gameData = null;
        }
        if (s_current == this)
            s_current = null;
    }

    nint Export(string name) =>
        NativeLibrary.TryGetExport(_library, name, out var address)
            ? address
            : throw new EntryPointNotFoundException($"{Path.GetFileName(_corePath)} is not a libretro core (missing {name}).");

    /// <summary>Returns a UTF-8 copy that stays alive until the host is disposed (cores may keep the pointer).</summary>
    byte* NativeString(string value)
    {
        if (!_nativeStrings.TryGetValue(value, out var p))
        {
            p = Marshal.StringToCoTaskMemUTF8(value);
            _nativeStrings[value] = p;
        }
        return (byte*)p;
    }

    static string Utf8(byte* p) => p == null ? string.Empty : Marshal.PtrToStringUTF8((nint)p) ?? string.Empty;

    void Log(LogLevel level, string message)
    {
        if (level >= _options.MinimumLogLevel)
            _options.Log?.Invoke(level, message);
    }

    // ---- Environment ---------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte OnEnvironment(uint cmd, void* data)
    {
        var host = s_current;
        if (host == null)
            return 0;
        try
        {
            return host.HandleEnvironment(cmd, data) ? (byte)1 : (byte)0;
        }
        catch (Exception ex)
        {
            host.Log(LogLevel.Error, $"Environment command {cmd} threw: {ex.Message}");
            return 0;
        }
    }

    bool HandleEnvironment(uint cmd, void* data)
    {
        switch (cmd)
        {
            case RetroEnv.SetRotation:
                Rotation = *(uint*)data;
                return true;
            case RetroEnv.GetOverscan:
                *(byte*)data = 0;
                return true;
            case RetroEnv.GetCanDupe:
                *(byte*)data = 1;
                return true;
            case RetroEnv.SetMessage:
                Log(LogLevel.Info, "[core message] " + Utf8(((RetroMessage*)data)->Msg));
                return true;
            case RetroEnv.SetMessageExt:
                Log(LogLevel.Info, "[core message] " + Utf8(((RetroMessageExt*)data)->Msg));
                return true;
            case RetroEnv.Shutdown:
                ShutdownRequested = true;
                return true;
            case RetroEnv.GetSystemDirectory:
                *(byte**)data = NativeString(Path.GetFullPath(_options.SystemDirectory));
                return true;
            case RetroEnv.GetSaveDirectory:
                *(byte**)data = NativeString(Path.GetFullPath(_options.SaveDirectory));
                return true;
            case RetroEnv.GetCoreAssetsDirectory:
                *(byte**)data = NativeString(Path.GetFullPath(_options.SystemDirectory));
                return true;
            case RetroEnv.GetLibretroPath:
                *(byte**)data = NativeString(_corePath);
                return true;
            case RetroEnv.GetUsername:
                *(byte**)data = NativeString("Player");
                return true;
            case RetroEnv.GetLanguage:
                *(uint*)data = 0; // English
                return true;
            case RetroEnv.SetPixelFormat:
            {
                var format = *(PixelFormat*)data;
                if (!Enum.IsDefined(format))
                    return false;
                PixelFormat = format;
                return true;
            }
            case RetroEnv.GetLogInterface:
                *(delegate* unmanaged[Cdecl]<LogLevel, byte*, nint, nint, nint, nint, nint, nint, nint, nint, void>*)data = &OnLog;
                return true;
            case RetroEnv.GetCoreOptionsVersion:
                // Version 0 makes cores declare options through the simple SET_VARIABLES path
                // (libretro-common converts v1/v2 definitions down for us).
                *(uint*)data = 0;
                return true;
            case RetroEnv.SetVariables:
                RegisterVariables((RetroVariable*)data);
                return true;
            case RetroEnv.GetVariable:
            {
                var variable = (RetroVariable*)data;
                var key = Utf8(variable->Key);
                if (_coreOptions.TryGetValue(key, out var option))
                {
                    variable->Value = NativeString(option.Value);
                    return true;
                }
                variable->Value = null;
                return false;
            }
            case RetroEnv.GetVariableUpdate:
                *(byte*)data = _optionsDirty ? (byte)1 : (byte)0;
                _optionsDirty = false;
                return true;
            case RetroEnv.SetVariable:
            {
                if (data == null)
                    return true; // query: "is SET_VARIABLE supported?"
                var variable = (RetroVariable*)data;
                if (_coreOptions.TryGetValue(Utf8(variable->Key), out var option))
                {
                    option.Value = Utf8(variable->Value);
                    return true;
                }
                return false;
            }
            case RetroEnv.SetCoreOptionsDisplay:
                return true;
            case RetroEnv.SetGeometry:
            {
                var av = AvInfo;
                av.Geometry = *(GameGeometry*)data;
                AvInfo = av;
                return true;
            }
            case RetroEnv.SetSystemAvInfo:
                AvInfo = *(SystemAvInfo*)data;
                return true;
            case RetroEnv.GetInputDeviceCapabilities:
                *(ulong*)data = (1UL << (int)RetroDevice.Joypad) | (1UL << (int)RetroDevice.Analog);
                return true;
            case RetroEnv.GetInputMaxUsers:
                *(uint*)data = 4;
                return true;
            case RetroEnv.GetInputBitmasks:
                return true;
            case RetroEnv.GetAudioVideoEnable:
                *(int*)data = 1 | 2; // video and audio enabled
                return true;
            case RetroEnv.GetFastForwarding:
                *(byte*)data = 0;
                return true;
            case RetroEnv.GetTargetRefreshRate:
                *(float*)data = 60f;
                return true;
            case RetroEnv.GetSavestateContext:
                if (data != null)
                    *(int*)data = 0; // RETRO_SAVESTATE_CONTEXT_NORMAL: states are for the user, not runahead/rollback
                return true;
            case RetroEnv.GetMessageInterfaceVersion:
                *(uint*)data = 1;
                return true;
            case RetroEnv.SetPerformanceLevel:
            case RetroEnv.SetInputDescriptors:
            case RetroEnv.SetControllerInfo:
            case RetroEnv.SetSubsystemInfo:
            case RetroEnv.SetSupportAchievements:
            case RetroEnv.SetSerializationQuirks:
            case RetroEnv.SetContentInfoOverride:
            case RetroEnv.SetMemoryMaps:
                return true; // accepted, not used yet
            case RetroEnv.SetHwRender:
                Log(LogLevel.Warn, "Core requested hardware rendering, which this frontend does not support.");
                return false;
            default:
                if (_reportedUnknownEnv.Add(cmd))
                    Log(LogLevel.Debug, $"Unhandled environment command {cmd & 0xFFFF}{((cmd & RetroEnv.Experimental) != 0 ? " (experimental)" : "")}");
                return false;
        }
    }

    void RegisterVariables(RetroVariable* vars)
    {
        for (; vars != null && vars->Key != null; vars++)
        {
            var option = CoreOption.Parse(Utf8(vars->Key), Utf8(vars->Value));
            if (_options.OptionOverrides.TryGetValue(option.Key, out var overrideValue) && option.Values.Contains(overrideValue))
                option.Value = overrideValue;
            _coreOptions[option.Key] = option;
        }
    }

    // ---- Logging -------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnLog(LogLevel level, byte* fmt, nint a0, nint a1, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7)
    {
        var host = s_current;
        if (host == null || level < host._options.MinimumLogLevel || host._options.Log == null)
            return;
        try
        {
            ReadOnlySpan<nint> args = [a0, a1, a2, a3, a4, a5, a6, a7];
            host._options.Log(level, CFormat.Format(fmt, args));
        }
        catch
        {
            // Never let an exception unwind into native code.
        }
    }

    // ---- Video / audio / input -----------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnVideoRefresh(void* data, uint width, uint height, nuint pitch)
    {
        var host = s_current;
        if (host == null || data == null)
            return; // null data = duplicate of the previous frame
        host.LastFrame.CopyFrom(data, (int)width, (int)height, (int)pitch, host.PixelFormat, host.FrameCount);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnAudioSample(short left, short right)
    {
        var host = s_current;
        if (host == null)
            return;
        host.AudioFramesReceived++;
        if (host._options.Audio is { } sink)
        {
            ReadOnlySpan<short> frame = [left, right];
            sink.Write(frame);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static nuint OnAudioSampleBatch(short* data, nuint frames)
    {
        var host = s_current;
        if (host == null)
            return frames;
        host.AudioFramesReceived += (long)frames;
        host._options.Audio?.Write(new ReadOnlySpan<short>(data, (int)frames * 2));
        return frames;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnInputPoll() => s_current?._options.Input?.Poll();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static short OnInputState(uint port, uint device, uint index, uint id)
    {
        var input = s_current?._options.Input;
        if (input == null)
            return 0;
        if (device == RetroDevice.Joypad && id == RetroConst.JoypadMask)
        {
            var mask = 0;
            for (uint button = 0; button < 16; button++)
                if (input.GetState(port, device, index, button) != 0)
                    mask |= 1 << (int)button;
            return (short)mask;
        }
        return input.GetState(port, device, index, id);
    }
}
