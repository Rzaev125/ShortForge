[CmdletBinding()]
param(
    [string]$LibraryFile = 'artifacts/smoke/editor-4f3b9603ccf647259bc198a30508bf81/data/library.sqlite3',
    [string]$Source = 'artifacts/asr-corpus/nasa-bennu-8-113.mp4'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$output = (New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/batch-' + [Guid]::NewGuid().ToString('N')))).FullName
$sync = (New-Item -ItemType Directory -Path (Join-Path $output 'sync')).FullName
$data = New-Item -ItemType Directory -Path (Join-Path $output 'data')
Copy-Item -LiteralPath $LibraryFile -Destination (Join-Path $data.FullName 'library.sqlite3')
$runner = Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe'
$bin = Join-Path $root 'tools/bin'
$log = Join-Path $output 'results.txt'
$original = (Get-Item -LiteralPath $Source).FullName
$fixture = Join-Path $sync "A-V O'Brien.mkv"
$previous = $env:SHORTFORGE_FFMPEG_BIN

function Invoke-Smoke($phase, $evidence, $sourcePath) {
    & $runner $phase $evidence $sourcePath | Tee-Object -FilePath $log -Append
    $code = $LASTEXITCODE
    if ($phase -eq 'batch-crash') {
        if (!$code -or !(Test-Path -LiteralPath (Join-Path $evidence 'crash-marker.txt'))) {
            throw 'The isolated WPF process did not reach the expected hard-stop point.'
        }
    } elseif ($code) { throw "WPF scenario failed: $phase" }
}
try {
    Write-Host "Evidence: $output"
    $env:SHORTFORGE_FFMPEG_BIN = $bin
    $before = (Get-FileHash -LiteralPath $original).Hash
    # Each 100 ms flash has an aligned beep on audio 0. Audio 1 is silent and is used by the real cached proxy.
    $pattern = "drawbox=color=white:t=fill:enable='between(n,30,32)+between(n,90,92)+between(n,150,152)'"
    $beep = "aevalsrc=0.35*sin(2*PI*880*t)*(between(t\,1\,1.1)+between(t\,3\,3.1)+between(t\,5\,5.1)):s=48000:d=6"
    & (Join-Path $bin 'ffmpeg.exe') -v error -nostdin -n -f lavfi -i 'color=black:s=320x180:r=30:d=6' -f lavfi -i $beep -f lavfi -i 'anullsrc=r=48000:cl=mono:d=6' -map 0:v -map 1:a -map 2:a -vf $pattern -c:v ffv1 -threads:v 2 -c:a pcm_s16le $fixture
    if ($LASTEXITCODE) { throw 'Could not encode A/V fixture.' }
    $fixtureHash = (Get-FileHash -LiteralPath $fixture).Hash
    Invoke-Smoke 'batch-crash' $output $original
    Invoke-Smoke 'batch-restart' $output $original
    Invoke-Smoke 'batch-sync' $sync $fixture
    & node (Join-Path $PSScriptRoot 'Check-BatchExport.cjs') $output $sync $fixture | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE) { throw 'Independent inspection or external playback failed.' }
    $selection = Join-Path $output 'selection'
    $selectionData = New-Item -ItemType Directory -Path (Join-Path $selection 'data')
    Copy-Item -LiteralPath $LibraryFile -Destination (Join-Path $selectionData.FullName 'library.sqlite3')
    Invoke-Smoke 'batch-selected' $selection $original
    if ($before -ne (Get-FileHash -LiteralPath $original).Hash -or $fixtureHash -ne (Get-FileHash -LiteralPath $fixture).Hash) {
        throw 'A source changed during this run.'
    }
    "PASS SHA256 NASA $before and technical source $fixtureHash unchanged after all phases." | Tee-Object -FilePath $log -Append
} finally { $env:SHORTFORGE_FFMPEG_BIN = $previous }
