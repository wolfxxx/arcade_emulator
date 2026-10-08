# ArcadeEmulator

A custom arcade frontend for Windows, written in C# / .NET 8, that runs arcade ROMs through
[libretro](https://www.libretro.com/) cores (FinalBurn Neo, MAME 2003-Plus). The cores emulate the
hardware. This project owns everything the player sees and touches.

**Status: Phase 2 (ROM library) built.** Games run in a window with sound, keyboard and gamepad
input, and save states. A SQLite library checks every ROM zip against each core's DAT and explains
why a set won't run. Next is Phase 3: the on-screen game browser.

## Setup

```powershell
./tools/fetch-deps.ps1     # cores (libretro buildbot), core DATs, free test ROMs (mamedev.org)
dotnet test                # unit tests + headless core tests
```

## Play

```powershell
dotnet run --project src/Arcade.App -- roms/robby.zip
# options: --core fbneo|mame2003_plus|<path.dll>   --fullscreen   --verbose
```

| Keyboard | |
|---|---|
| Arrows | Move |
| Z X A S Q W (or Ctrl Alt Space Shift) | Buttons 1–6 |
| 5 / 1 | Insert coin / Start (player 1) |
| 6 / 2 | Insert coin / Start (player 2) |
| F2 / F4 | Save / load state |
| F3 · P · F12 | Reset · pause · screenshot |
| F11 or Alt+Enter · Esc | Fullscreen · quit |

Gamepads (Xbox, PlayStation, Switch and others via SDL) work out of the box and can be plugged in at any
time. The first pad is player 1, Back is coin, Start is start, and holding Back+Start quits.

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
| `src/Arcade.App` | The player: SDL3 window, OpenGL renderer (rotation, aspect, sharp-bilinear), audio with rate control, input, main loop |
| `src/Arcade.Library` | DAT parsing, CRC verification (incl. parent/BIOS sets), `SetClassifier` (status + core choice), SQLite `GameLibrary`, `LibraryScanner`, artwork lookup |
| `src/Arcade.Libretro` | libretro host: `CoreHost` (load/run/serialize, environment callbacks), `VideoFrame`, `PngEncoder`, `ScriptedInput` |
| `src/Arcade.Spike` | Headless runner from Phase 0: runs N frames, dumps a PNG, checks save-state determinism |
| `tests/Arcade.Tests` | xUnit tests; core tests are skipped when cores/ROMs haven't been fetched |
| `cores/ dats/ roms/ system/ saves/ artwork/ library.db` | Not committed. `system/` is for BIOS files (e.g. `neogeo.zip`) |

## ROMs and licensing

No commercial ROMs are included or downloaded. Each ROM set must match the version its core expects.
For example, FBNeo shows "Romset is unknown" for sets it doesn't recognise, even though loading reports
success, which is why the DAT check exists. The test ROMs are ones their rights holders released for
free non-commercial use. FBNeo and MAME 2003-Plus are licensed for non-commercial use only.
