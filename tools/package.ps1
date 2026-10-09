# Builds a portable copy of the app that runs on another Windows PC without installing anything:
# dist\ArcadeEmulator\ (and a zip of it) with Arcade.App.exe, the emulator cores and their game lists,
# themes and fonts, and empty folders for ROMs and artwork.
# Your own ROMs, saves, settings and library are never included. -WithFreeRoms adds only the four games
# their rights holders released for free non-commercial use (mamedev.org), so the copy has something to play.
# Run tools\fetch-deps.ps1 first, so the cores and DATs are there.
param([switch]$WithFreeRoms, [switch]$NoZip)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'ArcadeEmulator'

$cores = @('fbneo_libretro.dll', 'mame2003_plus_libretro.dll')
$dats = @('fbneo.dat', 'mame2003_plus.dat')
$freeRoms = @('gridlee', 'robby', 'alienar', 'supertnk')
foreach ($f in $cores) { if (-not (Test-Path (Join-Path $root "cores\$f"))) { throw "cores\$f is missing: run tools\fetch-deps.ps1 first." } }
foreach ($f in $dats) { if (-not (Test-Path (Join-Path $root "dats\$f"))) { throw "dats\$f is missing: run tools\fetch-deps.ps1 first." } }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

# One self-contained exe: the .NET runtime and native libraries (SDL3, SQLite) are inside it.
Write-Host "publish Arcade.App"
dotnet publish (Join-Path $root 'src\Arcade.App\Arcade.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

function Copy-Into($from, $to) {
    New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
    Copy-Item $from $to -Recurse -Force
}

Write-Host "copy cores, game lists, themes and fonts"
foreach ($f in $cores) { Copy-Into (Join-Path $root "cores\$f") (Join-Path $out "cores\$f") }
foreach ($f in $dats) { Copy-Into (Join-Path $root "dats\$f") (Join-Path $out "dats\$f") }
Copy-Into (Join-Path $root 'themes') (Join-Path $out 'themes')
Copy-Into (Join-Path $root 'assets') (Join-Path $out 'assets')
$hiscore = Join-Path $root 'system\fbneo\hiscore.dat'
if (Test-Path $hiscore) { Copy-Into $hiscore (Join-Path $out 'system\fbneo\hiscore.dat') }
foreach ($dir in 'roms', 'artwork\bezels', 'saves', 'shaders') { New-Item -ItemType Directory -Force (Join-Path $out $dir) | Out-Null }

if ($WithFreeRoms) {
    foreach ($rom in $freeRoms) {
        $zip = Join-Path $root "roms\$rom.zip"
        if (Test-Path $zip) { Copy-Item $zip (Join-Path $out "roms\$rom.zip") } else { Write-Warning "roms\$rom.zip not found; run tools\fetch-deps.ps1" }
    }
}

@"
Arcade Emulator (portable)

Start Arcade.App.exe. Everything it keeps (library, settings, save states, high scores) stays in
this folder, so the whole folder can be moved or copied to another PC.

Adding games:   put ROM zips in the roms folder (they're found on the first start), or use
                Options > Add ROM folder... to point at another folder. ROM sets must match the
                cores' versions (FinalBurn Neo, MAME 2003-Plus); Options > Rescan library re-checks them.
BIOS files:     put them (e.g. neogeo.zip) in the roms folder or in system.
Bezels:         Arcade.App.exe bezels  downloads artwork from The Bezel Project for your games.
Cores:          Options > Update emulator cores (or Arcade.App.exe update-cores) gets the newest
                builds from the libretro buildbot.
Controls:       Arcade.App.exe --help lists the keys; all of them can be changed in Options > Controls.
Cabinets:       Options > Cabinet setup (rotation, free play, cabinet mode that starts fullscreen).

The emulator cores are FinalBurn Neo and MAME 2003-Plus, from the libretro project. Both are licensed
for non-commercial use only: https://github.com/libretro/FBNeo/blob/master/src/license.txt and
https://github.com/libretro/mame2003-plus-libretro/blob/master/LICENSE.md
Fonts: Press Start 2P and VT323 (SIL Open Font License, see assets\fonts).
"@ | Set-Content (Join-Path $out 'README.txt') -Encoding UTF8

$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("built  {0} ({1:N0} MB)" -f $out, $size)

if (-not $NoZip) {
    $version = (git -C $root describe --tags --always 2>$null)
    if (-not $version) { $version = Get-Date -Format 'yyyyMMdd' }
    $zip = Join-Path $dist "ArcadeEmulator-$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("zipped {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}
