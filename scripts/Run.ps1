param([string]$FFmpegBin)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'src/ShortForge.Desktop/bin/Release/net10.0-windows/win-x64/ShortForge.exe'
if (!(Test-Path -LiteralPath $exe)) { & (Join-Path $PSScriptRoot 'Build.ps1') }
if (!$FFmpegBin) { $FFmpegBin = Join-Path $root 'tools/bin' }
if (Test-Path -LiteralPath $FFmpegBin -PathType Container) {
    $env:SHORTFORGE_FFMPEG_BIN = (Resolve-Path -LiteralPath $FFmpegBin).Path
}
& $exe
