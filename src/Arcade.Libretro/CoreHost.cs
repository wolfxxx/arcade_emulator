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

    /// <summary>What each control does in the loaded game, if the core said (most arcade cores do).</summary>
    public IReadOnlyList<InputDescriptor> InputDescriptors { get; private set; } = [];

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

    /// <summary>
    /// Frames to run ahead (0 = off). A game reacts to a button a frame or more after it is pressed;
    /// running ahead shows the picture from that many frames on, so the reaction appears at once.
    /// </summary>
    public int RunAhead { get; set; }

    /// <summary>Why run-ahead turned itself off for this game, or null.</summary>
    public string? RunAheadProblem { get; private set; }

    /// <summary>Emulates exactly one frame (and with <see cref="RunAhead"/>, shows a later one).</summary>
    public void RunFrame()
    {
        if (MeasureLag && _options.Input != null && _gameLoaded)
            ReadControlsAndMeasure(_options.Input);
        try
        {
            if (RunAhead > 0 && RunAheadProblem == null)
                RunFrameAhead(RunAhead);
            else
                _retroRun();
        }
        finally
        {
            _polledForFrame = false;
        }
        FrameCount++;
    }

    // ---- Measuring how long a game takes to answer the controls ----

    /// <summary>
    /// When on, each time the stick or a button changes, the game is played a few frames on in
    /// secret twice, with the controls as they were and as they are now; the first frame whose
    /// picture differs tells how long the game takes to answer (see <see cref="LagMeasured"/>).
    /// </summary>
    public bool MeasureLag { get; set; }

    /// <summary>Frames the game took to show a change of the controls: 0 = in the very frame it was read.</summary>
    public event Action<int>? LagMeasured;

    /// <summary>Timings thrown away because the game didn't replay the same way twice from its saved state.</summary>
    public int UnrepeatableTimings { get; private set; }

    const int LagWindow = 8;
    // Coin and Start are left out: credits and starting a game take their own time to show.
    const ushort LagIgnoredButtons = (1 << (int)JoypadButton.Select) | (1 << (int)JoypadButton.Start);
    readonly ushort[] _controls = new ushort[4], _previousControls = new ushort[4];
    readonly ulong[] _withOldControls = new ulong[LagWindow];
    ushort[]? _controlsOverride;
    bool _polledForFrame, _measuring, _haveControls;
    ulong _measuredPicture;
    byte[] _lagState = [];

    /// <summary>Reads the controls for this frame now (instead of when the core asks) to see if they changed.</summary>
    void ReadControlsAndMeasure(IInputSource input)
    {
        input.Poll();
        _polledForFrame = true;
        Array.Copy(_controls, _previousControls, _controls.Length);
        var changed = false;
        for (uint port = 0; port < _controls.Length; port++)
        {
            _controls[port] = (ushort)ReadInput(input, port, RetroDevice.Joypad, 0, RetroConst.JoypadMask);
            changed |= ((_controls[port] ^ _previousControls[port]) & ~LagIgnoredButtons) != 0;
        }
        if (changed && _haveControls)
            MeasureLagNow();
        _haveControls = true;
    }

    void MeasureLagNow()
    {
        var size = StateSize;
        if (size <= 0)
            return;
        if (_lagState.Length != size)
            _lagState = new byte[size];
        if (!TrySaveState(_lagState))
            return;
        _measuring = true;
        _audioOn = false;
        _inputFrozen = true;
        try
        {
            _controlsOverride = _previousControls;
            for (var i = 0; i < LagWindow; i++)
            {
                _measuredPicture = 0;
                _retroRun();
                _withOldControls[i] = _measuredPicture;
            }
            if (!RestoreLagState(size))
                return;
            _controlsOverride = _controls;
            var lag = -1;
            for (var i = 0; i < LagWindow && lag < 0; i++)
            {
                _measuredPicture = 0;
                _retroRun();
                if (_measuredPicture != _withOldControls[i])
                    lag = i;
            }
            if (!RestoreLagState(size))
                return;
            if (lag < 0)
                return;
            // Replay the old controls once more: if that doesn't give the same pictures, the
            // difference came from the core not returning exactly to its saved state, not from
            // the controls, so the timing can't be trusted.
            _controlsOverride = _previousControls;
            var repeatable = true;
            for (var i = 0; i <= lag && repeatable; i++)
            {
                _measuredPicture = 0;
                _retroRun();
                repeatable = _measuredPicture == _withOldControls[i];
            }
            if (!RestoreLagState(size))
                return;
            if (repeatable)
                LagMeasured?.Invoke(lag);
            else
                UnrepeatableTimings++;
        }
        finally
        {
            _measuring = false;
            _audioOn = true;
            _inputFrozen = false;
            _controlsOverride = null;
        }
    }

    bool RestoreLagState(int size)
    {
        fixed (byte* p = _lagState)
            if (_retroUnserialize(p, (nuint)size) != 0)
                return true;
        MeasureLag = false;
        Log(LogLevel.Warn, "Stopped timing the controls: the core couldn't load its state back");
        return false;
    }

    /// <summary>FNV-1a over the visible pixels of a picture still in the core's memory.</summary>
    static ulong HashPicture(byte* data, uint width, uint height, nuint pitch, int bytesPerPixel)
    {
        var hash = 14695981039346656037UL;
        var rowBytes = (int)width * bytesPerPixel;
        for (var y = 0; y < height; y++)
            foreach (var b in new ReadOnlySpan<byte>(data + (nuint)y * pitch, rowBytes))
                hash = (hash ^ b) * 1099511628211UL;
        return hash;
    }

    bool _videoOn = true, _audioOn = true, _inputFrozen, _runningAhead, _pictureSent;
    byte[] _aheadState = [];

    // Self-check: some games keep part of their state outside save states, and then the frames
    // run ahead come out different from the real ones. Every so often the real frame's picture is
    // compared with the one shown for it earlier; while the controls haven't changed they must match.
    const int CheckEvery = 30, MismatchesAllowed = 1;
    const ulong InputSeed = 14695981039346656037;
    readonly Queue<(ulong Input, ulong Picture)> _predictions = new();
    int _predictionsAhead, _checkCountdown = CheckEvery, _mismatches;
    ulong _inputRead;

    /// <summary>Times the self-check compared a real frame with the picture shown for it.</summary>
    public int RunAheadChecks { get; private set; }

    /// <summary>
    /// Runs the real frame with its sound but not its picture, saves the state, runs on
    /// <paramref name="frames"/> more frames with the same input to get their picture (in silence),
    /// then puts the state back. Input is only read for the real frame, so anything counting
    /// frames as they're read (free play's coin-then-start) runs at the normal rate.
    /// </summary>
    void RunFrameAhead(int frames)
    {
        var size = StateSize;
        if (size <= 0)
        {
            StopRunAhead("this game can't save its state");
            _retroRun();
            return;
        }
        if (_aheadState.Length != size)
            _aheadState = new byte[size];
        if (_predictionsAhead != frames)
        {
            _predictions.Clear();
            _predictionsAhead = frames;
        }

        try
        {
            var check = --_checkCountdown <= 0 && _predictions.Count == frames;
            _videoOn = check;
            _pictureSent = false;
            _inputRead = InputSeed;
            _retroRun();
            _videoOn = true;
            if (check)
            {
                _checkCountdown = CheckEvery;
                CheckPrediction();
            }
            var input = _inputRead;
            _runningAhead = true;
            if (!TrySaveState(_aheadState))
            {
                StopRunAhead("the core couldn't save its state");
                return;
            }
            _audioOn = false;
            _inputFrozen = true;
            for (var i = 1; i <= frames; i++)
            {
                _videoOn = i == frames;
                _retroRun();
            }
            _predictions.Enqueue((input, LastFrame.ComputeHash()));
            if (_predictions.Count > frames)
                _predictions.Dequeue();
            fixed (byte* p = _aheadState)
                if (_retroUnserialize(p, (nuint)size) == 0)
                    StopRunAhead("the core couldn't load its state back");
        }
        finally
        {
            _videoOn = _audioOn = true;
            _inputFrozen = _runningAhead = false;
        }
    }

    /// <summary>
    /// Called after a real frame drawn for checking. The oldest prediction is the picture shown for
    /// this frame; it was made with the input of its own frame, so it only counts if every real frame
    /// since then (this one included) read the same input.
    /// </summary>
    void CheckPrediction()
    {
        if (!_pictureSent)
            return; // the core repeated its last picture, which we never kept
        var (input, picture) = _predictions.Peek();
        if (input != _inputRead || _predictions.Any(p => p.Input != input))
            return;
        RunAheadChecks++;
        if (picture != LastFrame.ComputeHash() && ++_mismatches > MismatchesAllowed)
            StopRunAhead("the game doesn't play back exactly from a saved state");
    }

    void StopRunAhead(string reason)
    {
        RunAheadProblem = reason;
        Log(LogLevel.Warn, "Run-ahead is off: " + reason);
    }

    public void Reset() => _retroReset();

    /// <summary>Bytes a save state needs right now, or 0 if the game can't be saved.</summary>
    public int StateSize => (int)_retroSerializeSize();

    public byte[] SaveState()
    {
        var size = StateSize;
        if (size == 0)
            throw new NotSupportedException($"{Info.Name} does not support save states for this game.");
        var buffer = new byte[size];
        if (!TrySaveState(buffer))
            throw new InvalidOperationException("retro_serialize failed.");
        return buffer;
    }

    /// <summary>Saves into a buffer of exactly <see cref="StateSize"/> bytes without allocating (rewind does this every frame).</summary>
    public bool TrySaveState(Span<byte> buffer)
    {
        fixed (byte* p = buffer)
            return _retroSerialize(p, (nuint)buffer.Length) != 0;
    }

    /// <summary>Tells the core the game is being fast-forwarded (some skip work they don't need then).</summary>
    public bool FastForwarding { get; set; }

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
                // Bits: 1 video wanted, 2 audio wanted, 4 state saves are only for run-ahead (may
                // be faster), 8 audio is thrown away (the core can skip making it).
                *(int*)data = (_videoOn ? 1 : 0) | (_audioOn ? 2 : 8) | (_runningAhead ? 4 : 0);
                return true;
            case RetroEnv.GetFastForwarding:
                *(byte*)data = FastForwarding ? (byte)1 : (byte)0;
                return true;
            case RetroEnv.GetTargetRefreshRate:
                *(float*)data = 60f;
                return true;
            case RetroEnv.GetSavestateContext:
                if (data != null)
                    *(int*)data = 0; // NORMAL. (FBNeo leaves part of the game out of RUNAHEAD_SAME_INSTANCE states and then shows stale pictures.)
                return true;
            case RetroEnv.GetMessageInterfaceVersion:
                *(uint*)data = 1;
                return true;
            case RetroEnv.SetInputDescriptors:
            {
                var list = new List<InputDescriptor>();
                for (var d = (RetroInputDescriptor*)data; d != null && d->Description != null; d++)
                    list.Add(new InputDescriptor(d->Port, d->Device, d->Index, d->Id, Utf8(d->Description)));
                InputDescriptors = list;
                return true;
            }
            case RetroEnv.SetPerformanceLevel:
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
        if (host == null || data == null || !host._videoOn)
            return; // null data = duplicate of the previous frame
        if (host._measuring)
        {
            // Timing the controls: only which picture it is matters, and the one on show stays.
            host._measuredPicture = HashPicture((byte*)data, width, height, pitch, host.PixelFormat == PixelFormat.Xrgb8888 ? 4 : 2);
            return;
        }
        host.LastFrame.CopyFrom(data, (int)width, (int)height, (int)pitch, host.PixelFormat, host.FrameCount);
        host._pictureSent = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnAudioSample(short left, short right)
    {
        var host = s_current;
        if (host == null || !host._audioOn)
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
        if (host == null || !host._audioOn)
            return frames;
        host.AudioFramesReceived += (long)frames;
        host._options.Audio?.Write(new ReadOnlySpan<short>(data, (int)frames * 2));
        return frames;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnInputPoll()
    {
        if (s_current is { _inputFrozen: false, _polledForFrame: false } host)
            host._options.Input?.Poll();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static short OnInputState(uint port, uint device, uint index, uint id)
    {
        var host = s_current;
        if (host == null)
            return 0;
        if (host._controlsOverride is { } controls && device == RetroDevice.Joypad)
        {
            var bits = port < controls.Length ? controls[port] : 0;
            return (short)(id == RetroConst.JoypadMask ? bits : id < 16 ? (bits >> (int)id) & 1 : 0);
        }
        var value = ReadInput(host._options.Input, port, device, index, id);
        if (!host._inputFrozen)
            host._inputRead = (host._inputRead ^ (ushort)value ^ ((ulong)id << 16) ^ ((ulong)port << 32) ^ ((ulong)device << 40) ^ ((ulong)index << 48)) * 1099511628211;
        return value;
    }

    static short ReadInput(IInputSource? input, uint port, uint device, uint index, uint id)
    {
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
