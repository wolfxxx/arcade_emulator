# Downloads the libretro cores and the freely distributable test ROMs into the repo root.
# Cores: libretro buildbot nightlies (FBNeo and MAME 2003-Plus are non-commercial licensed).
# ROMs: only titles the rights holders released for free non-commercial use (https://www.mamedev.org/roms/).
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot

$cores = @('fbneo_libretro', 'mame2003_plus_libretro')
$roms = @('gridlee', 'robby', 'alienar', 'supertnk')

foreach ($dir in 'downloads', 'cores', 'roms', 'system', 'saves') {
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
