# ArcadeEmulator

A custom arcade frontend for Windows, written in C# / .NET 8, that runs arcade ROMs through
[libretro](https://www.libretro.com/) cores (FinalBurn Neo, MAME 2003-Plus). The cores emulate the
hardware. This project owns everything the player sees and touches.

**Status: Phase 6 (visual effects) built.** Start the app and you get a game list that works with a
joystick alone: categories, search, favourites, previews, a pause menu in game, themes, and an attract
mode that plays game demos when idle. Every key and button can be changed, per player, per device and
per game, and there are settings for cabinets (sideways monitors, free play, a locked-down cabinet
mode). In game there are save slots with pictures, rewind, fast-forward, slow motion, cheats and
lasting high scores. The picture can look like an arcade monitor (scanlines, CRT effects, curved
glass), with bezel artwork and size options, and any RetroArch GLSL shader preset can be added.
Next is Phase 7: polish (input-lag reduction, recording, achievements, netplay, an installer).

## Setup

```powershell
./tools/fetch-deps.ps1     # cores (libretro buildbot), core DATs, free test ROMs (mamedev.org), hiscore.dat
./tools/fetch-deps.ps1 -Cheats   # optional: FBNeo's cheat collection into system/fbneo/cheats
./tools/fetch-deps.ps1 -Shaders  # optional: libretro's ~600 GLSL shader presets into shaders/libretro
dotnet test                # unit tests + headless core tests
```

## Play

```powershell
dotnet run --project src/Arcade.App                 # the game list (scans roms/ on first start)
dotnet run --project src/Arcade.App -- robby        # or play one game directly
# options: --core fbneo|mame2003_plus|<path.dll>   --fullscreen   --windowed   --verbose   --no-kiosk
```

| Game list | Keyboard | Gamepad |
|---|---|---|
| Move · jump letter | Arrows (← → jump letter) · PgUp/PgDn | D-pad or stick · LT/RT page |
| Play | Enter, Z or 1 | Ⓐ or Start |
| Favourite · options | F · Tab | Ⓧ · Ⓨ |
| Category | Q / W | LB / RB |
| Search | / | – |
| Back / quit | Esc | Ⓑ |

| In game (defaults) | Keyboard | Gamepad |
|---|---|---|
| Move | Arrows | D-pad or left stick |
| Buttons 1–8 | Z X A S Q W E D (or Ctrl Alt Space Shift) | Ⓐ Ⓑ Ⓧ Ⓨ LB RB LT RT |
| Insert coin / Start | 5 / 1 | Back / Start |
| Player 2 | R F G H move · I O K L buttons · 6 coin · 2 start | second pad |
| Pause menu | Esc or P | Guide, or hold Back + Start |
| Save · load · reset | F2 · F4 · F3 | hold Back + RB · Back + LB |
| Rewind · fast-forward (hold) | Backspace · Tab | hold Back + LT · Back + RT |
| Slow motion on/off | F7 | |
| Screenshot · fullscreen | F12 · F11 or Alt+Enter | |

Gamepads (Xbox, PlayStation, Switch and others via SDL) and USB arcade sticks or encoders work out of the
box and can be plugged in at any time; they become players 1, 2… in the order they connect. The mouse
also works in the list (click, double-click to play, wheel to scroll). The menus follow player 1's
controls and every pad, so a cabinet's own buttons drive them: button 1 or Start selects, button 2
goes back, button 3 favourites, button 4 or coin opens options, buttons 5/6 switch category.

The pause menu has save/load state, reset, controls, picture, cheats, fullscreen, "use this screen as preview",
and a summary of the game's controls. Games without artwork get a preview picture taken automatically
the first time you play for more than 15 seconds.

**Save states:** each game has 8 slots, shown with a picture of the moment you saved (pause menu ›
Save state… / Load state…; the page buttons switch between saving and loading, button 3 deletes).
F2 and F4 save and load the slot you used last (marked ●). States are kept in
`states/<set>/slot<n>.state`; a state saved by an earlier version shows up as slot 1.

**Rewind, fast-forward and slow motion** (Options › Gameplay): hold Backspace to run the game backwards,
up to a minute by default (from 15 seconds to 5 minutes, or off). The app records the game every frame,
stored as compressed differences between frames (about 1 MB per minute for Gridlee; bigger games use more). Hold Tab to
fast-forward (2× to 8×); F7 turns slow motion (½× or ¼×) on and off. Sound speeds up or slows
down with the game, like a tape.

**Cheats** (pause menu › Cheats…) work for FBNeo games that have a cheat file in `system/fbneo/cheats/`.
`fetch-deps.ps1 -Cheats` downloads FBNeo's collection (about 3,400 games). Cheats last until you leave the game.
MAME 2003-Plus has no cheat support here.

**High scores** are kept between sessions: MAME 2003-Plus does it by itself, and FBNeo uses
`system/fbneo/hiscore.dat`, which `fetch-deps.ps1` downloads. A game only keeps its scores if its
core's hiscore.dat lists it; the app adds entries the cores lack (Super Tank) to MAME 2003-Plus's
copy in `system/mame2003-plus/`. **Play time** (menus and pauses not
counted) shows in the game's details beside the play count.

**Picture** (pause menu › Picture…, or Options › Picture… for all games): the menu sits at the side
so the game behind it changes as you go. Settings apply to all games, or to one game ("Settings for:
This game only").

- *Style*: **Sharp pixels** (the default: crisp, evenly sized pixels), **Scanlines** (each line of the
  game drawn as a soft beam, wider for bright colours), **CRT** (beams, a glow around bright areas and
  the tube's red/green/blue phosphor stripes, slots or dots) and **CRT, curved**. *Adjust style…* changes
  the style's own settings: scanline strength, curvature, glow, phosphor pattern, brightness and so on.
  Effects follow the game's own lines, so a vertical game has vertical scanlines, as on a real cabinet.
- *Size*: fit the screen, **whole multiples** (every line of the game the same height; best with
  scanlines and CRT styles) or stretch. *Shape*: the arcade monitor's 4:3, or square pixels (with
  whole multiples, that's pixel-perfect).
- *Background*: black, or a dim glow of the game's colours around it.
- *Bezel artwork*: put a PNG with a see-through window in `artwork/bezels/` named after the game
  (`artwork/bezels/robby.png`); clones use their parent's, and `default-horizontal.png`,
  `default-vertical.png` or `default.png` cover the rest. The window is found from the transparency;
  a `.json` next to the PNG (`{"x": 240, "y": 0, "width": 1440, "height": 1080}`, in image pixels)
  sets it exactly. Bezel packs made for RetroArch (e.g. The Bezel Project's 1920×1080 PNGs) work.

**More styles from RetroArch:** any GLSL preset (`.glslp`) in the `shaders/` folder, in any subfolder,
appears in the Style list. `fetch-deps.ps1 -Shaders` downloads libretro's collection; 597 of its 624
presets work as they are. `Arcade.App shaders --check` compiles and runs each preset on a test picture
and lists any that fail (the complex crt-royale family is among those that don't; the built-in CRT
style covers the same ground). Vulkan (`.slangp`) presets aren't supported.

**Controls** (Options › Controls, or Controls in the pause menu): pick a control and press the key or
button you want, or use "Set up every control in turn", which is the quickest way to set up a cabinet.
Each page shows what the button does in the current game, as the core names it (e.g. "Weak Punch").
Keys are per player. A pad or stick you change gets its own profile, so remapping an arcade stick
doesn't change the Xbox pad. The Hotkeys page sets the pause menu, save/load, reset, screenshot and
"back to game list". A hotkey on a button the game also uses (like Start) only works while the
**hotkey enable** button (default: Back) is held for half a second. While it's held, that button
doesn't reach the game. The **This game** page swaps which panel button presses which game button,
and turns the picture. Everything is saved in `controls.json`, which is readable and hand-editable
(`key:z`, `pad:south`, `pad:leftx-`, `joy:button3`, `joy:hat0up`, `joy:axis1+`).

**Cabinet setup** (Options › Cabinet setup):

| Setting | |
|---|---|
| Screen | Turns everything, menus included, for a monitor mounted on its side. The list moves under the preview on a tall screen. |
| Vertical games | Show vertical games upright (with bars) or turned to fill the screen; each game can override it |
| Stick turns with picture | When a game is turned, up on the stick stays up on screen. Turn it off if you rotate the monitor by hand. |
| Free play | Start inserts a coin by itself |
| Cabinet mode | Starts fullscreen with no mouse pointer; players can't reach settings or Quit. Operator: hold Back (Esc / Ⓑ) for 5 s in the game list, or start with `--no-kiosk`. |

**Attract mode:** after a few idle minutes in the list (set in Options), random games run their own
demos, muted. Press Enter / Ⓐ to play the game on screen, or any other button to return.

**Themes:** `themes/<name>/theme.json` sets colours, fonts (TTF files or Windows font names), corner
radius, scanlines, a background image, and which side the list is on. Midnight (dark neon) and
Cabinet (warm, pixel fonts, scanlines) are included. Copy one to make your own; it appears in Options.

**Core selection:** each ROM zip is checked by CRC32 against each core's DAT (its list of supported
sets and files). The first core that has every file wins, with FBNeo preferred. If no core matches, the
app lists what's missing instead of showing a black screen.

**Timing:** if the game's refresh rate is within 1% of the monitor's, the app runs one frame per vsync.
Otherwise it paces frames by the clock. Either way, audio rate control nudges playback speed by up to
±0.5% so sound neither crackles nor drifts.

## Library

```powershell
dotnet run --project src/Arcade.App -- scan            # check roms/ (or: scan D:\MAME\roms)
dotnet run --project src/Arcade.App -- list            # playable games; --all adds broken sets
dotnet run --project src/Arcade.App -- info gridlee    # details and per-core check results
dotnet run --project src/Arcade.App -- robby           # play by set name
```

Each zip gets one of these results: **playable**, **needs BIOS**, **needs parent set**, **wrong version**
(the zip holds files from a different release of the set), **missing files**, **unknown set**, or **BIOS**.
Playable games can also carry notes, for example a missing samples zip (Gridlee's sound effects) or a
driver the core marks as not working.

| Command | |
|---|---|
| `scan [folder ...]` | Add ROM folders and rescan. Only new or changed zips are reopened. |
| `set-core <set> <core\|auto>` | Always run a game on a given core |
| `favorite <set> [off]` · `folders [--remove <folder>]` | Favourites · library folders |

Optional extras: `dats/catver.ini` (genres) and `dats/nplayers.ini` are picked up when present.
Artwork goes in `artwork/`, either MAME-style (`artwork/snap/robby.png`) or as libretro-thumbnails
folders (`artwork/MAME/Named_Snaps/<title>.png`). Samples go in `system/fbneo/samples/` or
`system/mame2003-plus/samples/`. The library is stored in `library.db`. Delete it to start over.

Clones need their own zip, either a split set (with the parent zip next to it) or a non-merged set.
Clones inside a merged parent zip aren't listed, because cores load games by file name.

## Layout

| Path | Purpose |
|------|---------|
| `src/Arcade.App` | The app: SDL3 window and main loop (`ArcadeApp`), scenes (`Browser/`, `GameScene`), `GameSession` (a running core with speed, rewind and save states), `StateSlots` and `StatePicker` (save slots), UI toolkit in `Ui/` (batched GL renderer, FontStashSharp text, themes, menus, input), `Controls/` (bindings, `controls.json`, the `ControlMapper` from keys and buttons to the core, the controls screen), `Video/` (RetroArch GLSL preset reader and multi-pass `ShaderChain`, built-in styles in `Video/Shaders/`, picture layout, bezels, the Picture menu), `VideoRenderer`, audio/input, `ScreenRotator` |
| `src/Arcade.Library` | DAT parsing, CRC verification (incl. parent/BIOS sets), `SetClassifier` (status + core choice), SQLite `GameLibrary`, `LibraryScanner`, artwork lookup |
| `src/Arcade.Libretro` | libretro host: `CoreHost` (load/run/serialize, environment callbacks), `RewindBuffer` (XOR + LZ4 history in a ring), `VideoFrame`, `PngEncoder`, `ScriptedInput` |
| `src/Arcade.Spike` | Headless runner from Phase 0: runs N frames, dumps a PNG, checks save-state determinism |
| `tests/Arcade.Tests` | xUnit tests; core tests are skipped when cores/ROMs haven't been fetched |
| `themes/` · `assets/fonts/` | Bundled themes; Press Start 2P and VT323 fonts (SIL Open Font License, see the OFL files) |
| `cores/ dats/ roms/ system/ saves/ states/ artwork/ shaders/ library.db settings.json controls.json` | Not committed. `system/` is for BIOS files (e.g. `neogeo.zip`), `hiscore.dat` and cheats |

## ROMs and licensing

No commercial ROMs are included or downloaded. Each ROM set must match the version its core expects.
For example, FBNeo shows "Romset is unknown" for sets it doesn't recognise, even though loading reports
success, which is why the DAT check exists. The test ROMs are ones their rights holders released for
free non-commercial use. FBNeo and MAME 2003-Plus are licensed for non-commercial use only.

For automated UI checks the app can run hidden and scripted, e.g.
`Arcade.App --offscreen 1600x900 --script "wait 2; down; shot list.png; accept; wait 3; hotkey menu; shot pause.png; quit"`
(also `hold rewind 2` to hold a hotkey). Such runs don't change settings, play counts, previews,
save states or the cores' own save files (they save to a temporary folder).
