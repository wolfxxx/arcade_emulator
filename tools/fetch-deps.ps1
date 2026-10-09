# Downloads the libretro cores and the freely distributable test ROMs into the repo root.
# Cores: libretro buildbot nightlies (FBNeo and MAME 2003-Plus are non-commercial licensed).
# ROMs: only titles the rights holders released for free non-commercial use (https://www.mamedev.org/roms/).
# Also FBNeo's hiscore.dat (keeps high score tables), with -Cheats the FBNeo cheat collection, and with
# -Shaders libretro's GLSL shader collection (offered as picture styles; each shader has its own licence).
param([switch]$Force, [switch]$Cheats, [switch]$Shaders)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot

$cores = @('fbneo_libretro', 'mame2003_plus_libretro')
$roms = @('gridlee', 'robby', 'alienar', 'supertnk')
# Each core's list of supported ROM sets, used to pick the right core for a game.
$dats = @{
    'fbneo.dat'         = 'https://raw.githubusercontent.com/libretro/FBNeo/master/dats/FinalBurn%20Neo%20(ClrMame%20Pro%20XML%2C%20Arcade%20only).dat'
    'mame2003_plus.dat' = 'https://raw.githubusercontent.com/libretro/mame2003-plus-libretro/master/metadata/mame2003-plus.xml'
}

foreach ($dir in 'downloads', 'cores', 'dats', 'roms', 'system', 'saves') {
    New-Item -ItemType Directory -Force (Join-Path $root $dir) | Out-Null
}

foreach ($core in $cores) {
    $dll = Join-Path $root "cores\$core.dll"
    if ((Test-Path $dll) -and -not $Force) { Write-Host "have  $core"; continue }
    $zip = Join-Path $root "downloads\$core.dll.zip"
    Write-Host "fetch $core"
    Invoke-WebRequest "https://buildbot.libretro.com/nightly/windows/x86_64/latest/$core.dll.zip" -OutFile $zip
    Expand-Archive $zip -DestinationPath (Join-Path $root 'cores') -Force
}

foreach ($rom in $roms) {
    $out = Join-Path $root "roms\$rom.zip"
    if ((Test-Path $out) -and -not $Force) { Write-Host "have  $rom"; continue }
    Write-Host "fetch $rom"
    Invoke-WebRequest "https://www.mamedev.org/roms/$rom/$rom.zip" -OutFile $out
}

foreach ($dat in $dats.Keys) {
    $out = Join-Path $root "dats\$dat"
    if ((Test-Path $out) -and -not $Force) { Write-Host "have  $dat"; continue }
    Write-Host "fetch $dat"
    Invoke-WebRequest $dats[$dat] -OutFile $out
}

# High score tables (FBNeo looks for system/fbneo/hiscore.dat; MAME 2003-Plus has its own built in).
$hiscore = Join-Path $root 'system\fbneo\hiscore.dat'
New-Item -ItemType Directory -Force (Split-Path $hiscore) | Out-Null
if ((Test-Path $hiscore) -and -not $Force) { Write-Host "have  hiscore.dat" }
else {
    Write-Host "fetch hiscore.dat"
    Invoke-WebRequest 'https://raw.githubusercontent.com/libretro/FBNeo/master/metadata/hiscore.dat' -OutFile $hiscore
}

# Cheats for FBNeo games (about 3,400 files), shown in the pause menu under Cheats.
if ($Cheats) {
    $cheatDir = Join-Path $root 'system\fbneo\cheats'
    $zip = Join-Path $root 'downloads\fbneo-cheats.zip'
    Write-Host "fetch FBNeo cheats"
    Invoke-WebRequest 'https://github.com/finalburnneo/FBNeo-cheats/archive/refs/heads/master.zip' -OutFile $zip
    $unpacked = Join-Path $root 'downloads\fbneo-cheats'
    Expand-Archive $zip -DestinationPath $unpacked -Force
    New-Item -ItemType Directory -Force $cheatDir | Out-Null
    Copy-Item (Join-Path $unpacked 'FBNeo-cheats-master\cheats\*') $cheatDir -Recurse -Force
    Write-Host "      $((Get-ChildItem $cheatDir).Count) cheat files in system\fbneo\cheats"
}

# libretro's GLSL shaders (about 600 presets), listed under Picture > Style. Most work as they are;
# `Arcade.App shaders --check` tests each one.
if ($Shaders) {
    $shaderDir = Join-Path $root 'shaders\libretro'
    $zip = Join-Path $root 'downloads\glsl-shaders.zip'
    Write-Host "fetch libretro GLSL shaders"
    Invoke-WebRequest 'https://github.com/libretro/glsl-shaders/archive/refs/heads/master.zip' -OutFile $zip
    $unpacked = Join-Path $root 'downloads\glsl-shaders'
    Expand-Archive $zip -DestinationPath $unpacked -Force
    New-Item -ItemType Directory -Force $shaderDir | Out-Null
    Copy-Item (Join-Path $unpacked 'glsl-shaders-master\*') $shaderDir -Recurse -Force
    Write-Host "      $((Get-ChildItem $shaderDir -Recurse -Filter *.glslp).Count) presets in shaders\libretro"
}