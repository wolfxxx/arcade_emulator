using System.Runtime.InteropServices;

namespace Arcade.Libretro;

// Mirrors of the parts of libretro.h we use. Field layout must match the C ABI exactly.
// C `bool` is one byte, so it is mapped to `byte` everywhere it crosses the boundary.

public enum PixelFormat : int
{
    Rgb1555 = 0, // 0RGB1555, the libretro default
    Xrgb8888 = 1,
    Rgb565 = 2,
}

public enum LogLevel : int
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public static class RetroDevice
{
    public const uint None = 0;
    public const uint Joypad = 1;
    public const uint Mouse = 2;
    public const uint Keyboard = 3;
    public const uint LightGun = 4;
    public const uint Analog = 5;
    public const uint Pointer = 6;
}

public enum JoypadButton : uint
{
    B = 0,
    Y = 1,
    Select = 2, // insert coin on arcade cores
    Start = 3,
    Up = 4,
    Down = 5,
    Left = 6,
    Right = 7,
    A = 8,
    X = 9,
    L = 10,
    R = 11,
    L2 = 12,
    R2 = 13,
    L3 = 14,
    R3 = 15,
}

internal static class RetroEnv
{
    public const uint Experimental = 0x10000;

    public const uint SetRotation = 1;
    public const uint GetOverscan = 2;
    public const uint GetCanDupe = 3;
    public const uint SetMessage = 6;
    public const uint Shutdown = 7;
    public const uint SetPerformanceLevel = 8;
    public const uint GetSystemDirectory = 9;
    public const uint SetPixelFormat = 10;
    public const uint SetInputDescriptors = 11;
    public const uint SetHwRender = 14;
    public const uint GetVariable = 15;
    public const uint SetVariables = 16;
    public const uint GetVariableUpdate = 17;
    public const uint SetSupportNoGame = 18;
    public const uint GetLibretroPath = 19;
    public const uint GetInputDeviceCapabilities = 24;
    public const uint GetLogInterface = 27;
    public const uint GetPerfInterface = 28;
    public const uint GetCoreAssetsDirectory = 30;
    public const uint GetSaveDirectory = 31;
    public const uint SetSystemAvInfo = 32;
    public const uint SetSubsystemInfo = 34;
    public const uint SetControllerInfo = 35;
    public const uint SetMemoryMaps = 36 | Experimental;
    public const uint SetGeometry = 37;
    public const uint GetUsername = 38;
    public const uint GetLanguage = 39;
    public const uint SetSupportAchievements = 42 | Experimental;
    public const uint SetSerializationQuirks = 44;
    public const uint GetVfsInterface = 45 | Experimental;
    public const uint GetLedInterface = 46 | Experimental;
    public const uint GetAudioVideoEnable = 47 | Experimental;
    public const uint GetFastForwarding = 49 | Experimental;
    public const uint GetTargetRefreshRate = 50 | Experimental;
    public const uint GetInputBitmasks = 51 | Experimental;
    public const uint GetCoreOptionsVersion = 52;
    public const uint SetCoreOptionsDisplay = 55;
    public const uint GetMessageInterfaceVersion = 59;
    public const uint SetMessageExt = 60;
    public const uint GetInputMaxUsers = 61;
    public const uint SetContentInfoOverride = 65;
    public const uint SetVariable = 70;
    public const uint GetSavestateContext = 72 | Experimental;
}

internal static class RetroConst
{
    public const uint ApiVersion = 1;
    public const uint JoypadMask = 256; // RETRO_DEVICE_ID_JOYPAD_MASK
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RetroSystemInfo
{
    public byte* LibraryName;
    public byte* LibraryVersion;
    public byte* ValidExtensions;
    public byte NeedFullpath;
    public byte BlockExtract;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RetroGameInfo
{
    public byte* Path;
    public void* Data;
    public nuint Size;
    public byte* Meta;
}

[StructLayout(LayoutKind.Sequential)]
public struct GameGeometry
{
    public uint BaseWidth;
    public uint BaseHeight;
    public uint MaxWidth;
    public uint MaxHeight;
    public float AspectRatio;
}

[StructLayout(LayoutKind.Sequential)]
public struct SystemTiming
{
    public double Fps;
    public double SampleRate;
}

[StructLayout(LayoutKind.Sequential)]
public struct SystemAvInfo
{
    public GameGeometry Geometry;
    public SystemTiming Timing;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RetroVariable
{
    public byte* Key;
    public byte* Value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RetroInputDescriptor
{
    public uint Port;
    public uint Device;
    public uint Index;
    public uint Id;
    public byte* Description;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RetroMessage
{
    public byte* Msg;
    public uint Frames;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RetroMessageExt
{
    public byte* Msg;
    public uint Duration;
    public uint Priority;
    public int Level;
    public int Target;
    public int Type;
    public sbyte Progress;
}
