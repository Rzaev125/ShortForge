[CmdletBinding()]
param([Parameter(Mandatory)][string]$Source, [ValidateSet('en','ru','az','auto')][string]$Language='en', [ValidateRange(0,100)][int]$AudioTrack=0)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$file = (Get-Item -LiteralPath $Source).FullName
$bin = Join-Path $root 'tools/bin'
$app = Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe'
$output = (New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/asr-' + [Guid]::NewGuid().ToString('N')))).FullName
Write-Host "Evidence directory: $output"
$before = (Get-FileHash -LiteralPath $file).Hash
$previousBin = $env:SHORTFORGE_FFMPEG_BIN
$previousLanguage = $env:SHORTFORGE_TEST_LANGUAGE
$previousTrack = $env:SHORTFORGE_TEST_TRACK
try {
    $env:SHORTFORGE_FFMPEG_BIN = $bin
    $env:SHORTFORGE_TEST_LANGUAGE = $Language
    $env:SHORTFORGE_TEST_TRACK = $AudioTrack
    foreach ($phase in 'asr','asr-restart') {
        & $app $phase $output $file | Tee-Object -FilePath (Join-Path $output "$phase.txt")
        if ($LASTEXITCODE) { throw "$phase failed." }
    }
    $silentDir = (New-Item -ItemType Directory -Path (Join-Path $output 'silence')).FullName
    $quiet = Join-Path $silentDir 'silent.mp4'
    & (Join-Path $bin 'ffmpeg.exe') -v error -nostdin -n -f lavfi -i 'color=size=320x180:rate=10' -f lavfi -i 'anullsrc=r=16000:cl=mono' -t 12 -c:v libx264 -threads 2 -c:a aac $quiet
    if ($LASTEXITCODE) { throw 'Cannot create silence control.' }
    & $app asr-silence $silentDir $quiet | Tee-Object -FilePath (Join-Path $output 'silence.txt')
    if ($LASTEXITCODE) { throw 'Silence check failed.' }
    if ($before -ne (Get-FileHash -LiteralPath $file).Hash) {throw 'Source changed.'}
    'PASS: original SHA-256 preserved; see per-phase measurements.' | Tee-Object -FilePath (Join-Path $output 'results.txt')
} finally {
    $env:SHORTFORGE_FFMPEG_BIN = $previousBin
    $env:SHORTFORGE_TEST_LANGUAGE = $previousLanguage
    $env:SHORTFORGE_TEST_TRACK = $previousTrack
}
