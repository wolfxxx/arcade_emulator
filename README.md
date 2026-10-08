# ArcadeEmulator

A custom arcade frontend for Windows, written in C# / .NET 8, that runs arcade ROMs through
[libretro](https://www.libretro.com/) cores (FinalBurn Neo, MAME 2003-Plus). The cores emulate the
hardware. This project owns everything the player sees and touches.

**Status: Phase 0 (interop spike) complete.** Cores load, run headless, produce video, audio, and
save states, and accept input. The next step is Phase 1: a window, sound output, and controller input.

## Setup

```powershell
./tools/fetch-deps.ps1     # downloads cores (libretro buildbot) and free test ROMs (mamedev.org)
dotnet test                # unit tests + headless core tests
```

## Spike

```powershell
dotnet run --project src/Arcade.Spike -- cores/mame2003_plus_libretro.dll roms/robby.zip --frames 600
# options: --out out/frame.png  --play (scripted coin/start/moves)  --verbose (core log + unhandled env calls)
```

## Layout

| Path | Purpose |
|------|---------|
| `src/Arcade.Libretro` | libretro host: `CoreHost` (load/run/serialize, environment callbacks), `VideoFrame`, `PngEncoder`, `ScriptedInput` |
| `src/Arcade.Spike` | Headless runner: runs N frames, dumps a PNG, checks save-state determinism |
| `tests/Arcade.Tests` | xUnit tests; core tests are skipped when cores/ROMs haven't been fetched |
| `cores/ roms/ system/ saves/` | Not committed. `system/` is for BIOS files (e.g. `neogeo.zip`) |

## ROMs and licensing

No commercial ROMs are included or downloaded. Each ROM set must match the version its core expects.
For example, FBNeo shows "Romset is unknown" for sets it doesn't recognise, even though loading reports
success. The test ROMs are ones their rights holders released for free non-commercial use. FBNeo and
MAME 2003-Plus are licensed for non-commercial use only.
