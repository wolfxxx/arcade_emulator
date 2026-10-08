# ArcadeEmulator

A custom arcade frontend for Windows, written in C# / .NET 8, that runs arcade ROMs through
[libretro](https://www.libretro.com/) cores (FinalBurn Neo, MAME 2003-Plus). The cores emulate the
hardware. This project owns everything the player sees and touches.

**Status: Phase 1 (playable version) built.** Games run in a window with sound, keyboard and
gamepad input, save states, and automatic core selection. Next is Phase 2: the ROM library.

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

## Layout

| Path | Purpose |
|------|---------|
| `src/Arcade.App` | The player: SDL3 window, OpenGL renderer (rotation, aspect, sharp-bilinear), audio with rate control, input, main loop |
| `src/Arcade.Library` | `DatFile` (DAT parsing) and `RomSetChecker` (CRC verification incl. parent/BIOS sets) |
| `src/Arcade.Libretro` | libretro host: `CoreHost` (load/run/serialize, environment callbacks), `VideoFrame`, `PngEncoder`, `ScriptedInput` |
| `src/Arcade.Spike` | Headless runner from Phase 0: runs N frames, dumps a PNG, checks save-state determinism |
| `tests/Arcade.Tests` | xUnit tests; core tests are skipped when cores/ROMs haven't been fetched |
| `cores/ dats/ roms/ system/ saves/` | Not committed. `system/` is for BIOS files (e.g. `neogeo.zip`) |

## ROMs and licensing

No commercial ROMs are included or downloaded. Each ROM set must match the version its core expects.
For example, FBNeo shows "Romset is unknown" for sets it doesn't recognise, even though loading reports
success, which is why the DAT check exists. The test ROMs are ones their rights holders released for
free non-commercial use. FBNeo and MAME 2003-Plus are licensed for non-commercial use only.
