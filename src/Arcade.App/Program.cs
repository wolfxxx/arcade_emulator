using Arcade.App;
using SDL;
using static SDL.SDL3;

const string Usage = """
    Usage: Arcade.App <rom.zip> [--core fbneo|mame2003_plus|<path.dll>] [--fullscreen] [--verbose]

    Keys:  arrows move · Z X A S Q W (or Ctrl Alt Space Shift) buttons 1-6
           5 coin · 1 start · 6/2 coin/start player 2
           F2 save state · F4 load state · F3 reset · P pause · F12 screenshot
           F11 or Alt+Enter fullscreen · Esc quit
    Pads:  any XInput/PlayStation/Switch controller; Back = coin, Start = start,
           hold Back+Start to quit
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 2 : 0;
}

string? rom = null, core = null, screenshot = null;
bool fullscreen = false, verbose = false;
double? exitAfter = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--core": core = args[++i]; break;
        case "--fullscreen": fullscreen = true; break;
        case "--verbose": verbose = true; break;
        // For automated checks: run for N seconds, optionally capture the window, then exit.
        case "--exit-after": exitAfter = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--screenshot": screenshot = args[++i]; break;
        default:
            if (args[i].StartsWith('-') || rom != null)
            {
                Console.Error.WriteLine($"Unknown argument '{args[i]}'.\n\n{Usage}");
                return 2;
            }
            rom = args[i];
            break;
    }
}

if (rom == null || !File.Exists(rom))
{
    Console.Error.WriteLine(rom == null ? Usage : $"ROM not found: {rom}");
    return 2;
}

try
{
    return new ArcadeApp(new AppOptions(rom, core, fullscreen, verbose, screenshot, exitAfter)).Run();
}
catch (Exception ex) when (ex is RomSetException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
{
    Console.Error.WriteLine(ex.Message);
    unsafe
    {
        // Also show it in a dialog, since a frontend launch won't have a visible console.
        SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, "Arcade", ex.Message, null);
    }
    return 1;
}
