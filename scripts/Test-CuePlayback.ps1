[CmdletBinding()]
param(
    [string]$LibraryFile = 'artifacts/smoke/editor-4f3b9603ccf647259bc198a30508bf81/data/library.sqlite3',
    [string]$Source = 'artifacts/asr-corpus/nasa-bennu-8-113.mp4'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$output = (New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/cue-playback-' + [Guid]::NewGuid().ToString('N')))).FullName
$data = New-Item -ItemType Directory -Path (Join-Path $output 'data')
Copy-Item -LiteralPath $LibraryFile -Destination (Join-Path $data.FullName 'library.sqlite3')
$runner = Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe'
$previous = $env:SHORTFORGE_FFMPEG_BIN
try {
    $env:SHORTFORGE_FFMPEG_BIN = Join-Path $root 'tools/bin'
    Write-Host "Evidence: $output"
    & $runner cue-playback $output (Get-Item -LiteralPath $Source).FullName | Tee-Object -FilePath (Join-Path $output 'results.txt')
    if ($LASTEXITCODE) { throw 'WPF cue playback acceptance failed.' }
} finally { $env:SHORTFORGE_FFMPEG_BIN = $previous }
