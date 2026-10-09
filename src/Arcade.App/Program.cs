using Arcade.App;
using Arcade.Library;
using SDL;
using static SDL.SDL3;

const string Usage = """
    Usage: Arcade.App                      open the game list
           Arcade.App <rom.zip | set name> play one game directly
    Options: --core fbneo|mame2003_plus|<path.dll>   --fullscreen   --windowed   --verbose
             --no-kiosk   start normally even if cabinet mode is on

    Library:
      scan [folder ...]           add ROM folders (default: roms) and check every zip
      list [text] [--all] [--favorites]
                                  list playable games (--all also shows broken sets)
      info <set>                  details, per-core check results and artwork found
      set-core <set> <core|auto>  always run a game on a given core
      favorite <set> [off]        mark or unmark a favourite
      folders [--remove <folder>] show or remove library folders
      bezels                      download bezel artwork (The Bezel Project) for games that have none
      update-cores                install or update the emulator cores and their game lists, then rescan
      shaders [--check] [folder]  list picture styles; --check tests each preset in a hidden window

    Game list: arrows move · Enter play · F favourite · Tab options · / search
               Q/W category · ←/→ jump letter · Esc quit
    In game:   arrows move · Z X A S Q W E D (or Ctrl Alt Space Shift) buttons 1-8
               5 coin · 1 start · player 2: R F G H move, I O K L buttons, 6 coin, 2 start
               Esc pause menu · F2 save state · F4 load state · F3 reset · F12 screenshot
               Backspace (hold) rewind · Tab (hold) fast-forward · F7 slow motion
               F11 or Alt+Enter fullscreen
    Pads:      any XInput/PlayStation/Switch controller, or a USB arcade stick/encoder.
               Back = coin, Start = start; Guide, or hold Back + Start, for the pause menu
    Controls:  all of these can be changed in Options > Controls (saved in controls.json)
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length > 0 && args[0] is "-h" or "--help" or "/?")
{
    Console.WriteLine(Usage);
    return 0;
}

var paths = AppPaths.Discover();
CoreUpdater.CleanUp(paths);
if (args.Length > 0 && LibraryCommands.Names.Contains(args[0]) && !File.Exists(args[0]))
    return new LibraryCommands(paths).Run(args[0], args[1..]);
if (args.Length > 0 && args[0] == "shaders" && !File.Exists(args[0]))
    return Arcade.App.Video.ShaderCommand.Run(paths, args[1..]);

string? rom = null, core = null, script = null;
bool? fullscreen = null;
var verbose = false;
var noKiosk = false;
(int, int)? offscreen = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--core": core = args[++i]; break;
        case "--fullscreen": fullscreen = true; break;
        case "--windowed": fullscreen = false; break;
        case "--verbose": verbose = true; break;
        case "--no-kiosk": noKiosk = true; break;
        // For automated checks: drive the app with a script, optionally rendering hidden at a fixed size.
        case "--script": script = args[++i]; break;
        case "--offscreen":
            var size = args[++i].Split('x');
            offscreen = (int.Parse(size[0]), int.Parse(size[1]));
            break;
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

// A bare set name ("robby") is looked up in the library, which also supplies any core the user chose for it.
string? setName = null;
if (rom != null && !File.Exists(rom) && File.Exists(paths.LibraryDb))
{
    using var library = new GameLibrary(paths.LibraryDb);
    if (library.Find(rom) is { } game)
    {
        setName = game.SetName;
        rom = game.Path;
        core ??= game.CoreOverride;
    }
}

if (rom != null && !File.Exists(rom))
{
    Console.Error.WriteLine($"ROM not found: {rom}" + (setName == null ? " (not a file, and not a set in the library — run scan?)" : ""));
    return 2;
}

try
{
    return new ArcadeApp(new AppOptions(rom, core, fullscreen, verbose, script, offscreen, noKiosk)).Run();
}
catch (Exception ex) when (ex is RomSetException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
{
    Console.Error.WriteLine(ex.Message);
    unsafe
    {
        // Also show it in a dialog, since a frontend launch won't have a visible console.
        if (offscreen == null)
            SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, "Arcade", ex.Message, null);
    }
    return 1;
}
