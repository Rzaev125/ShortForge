# B06. Generate a real, seekable MP4 in a new isolated smoke profile.
[CmdletBinding()]
param([string]$FFmpegBin, [switch]$KeepMedia)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (-not $FFmpegBin) { $FFmpegBin = Join-Path $root 'tools/bin' }
$bin = (Resolve-Path -LiteralPath $FFmpegBin).Path
$encoder = (Get-Item -LiteralPath (Join-Path $bin 'ffmpeg.exe')).FullName
$probe = (Get-Item -LiteralPath (Join-Path $bin 'ffprobe.exe')).FullName
$app = (Get-Item -LiteralPath (Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe')).FullName
$evidence = (New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/b06-' + [Guid]::NewGuid().ToString('N')))).FullName
$seed = Join-Path $evidence 'seed.mp4'
$source = Join-Path $evidence "B06 import O'Brien.mp4"
$previousBin = $env:SHORTFORGE_FFMPEG_BIN
Write-Host "Evidence directory: $evidence"
try {
    $env:SHORTFORGE_FFMPEG_BIN = $bin
    & $encoder -v error -nostdin -n -f lavfi -i 'testsrc2=size=1280x720:rate=24' -t 2 -c:v libx264 -threads 2 -preset ultrafast -crf 14 -g 24 -pix_fmt yuv420p $seed
    if ($LASTEXITCODE) { throw 'Cannot encode B06 seed.' }
    & $encoder -v error -nostdin -n -stream_loop -1 -i $seed -c copy -fs 1150000000 -movflags +faststart $source
    if ($LASTEXITCODE) { throw 'Cannot prepare B06 video.' }
    $metadata = & $probe -v error -show_entries 'stream=codec_name,width,height:format=duration,size' -of json $source
    if ($LASTEXITCODE) { throw 'B06 probe failed.' }
    $metadata | Set-Content -LiteralPath (Join-Path $evidence 'ffprobe.json') -Encoding utf8
    $media = $metadata -join [Environment]::NewLine | ConvertFrom-Json
    if ([long]$media.format.size -le 1GB -or $media.streams[0].codec_name -ne 'h264') { throw 'Invalid B06 fixture.' }
    $before = (Get-FileHash -LiteralPath $source).Hash
    & $app large $evidence $source | Tee-Object -FilePath (Join-Path $evidence 'large-import.txt')
    if ($LASTEXITCODE) { throw "B06 failed (exit $LASTEXITCODE)." }
    if ($before -ne (Get-FileHash -LiteralPath $source).Hash -or
        $before -ne (Get-Content -Raw -LiteralPath (Join-Path $evidence 'import.sha256')).Trim()) {
        throw 'Source, stored fingerprint and pre-import fingerprint must match.'
    }
    'PASS B06. Original hash verified before import, in SQLite and after playback.' | Tee-Object -Append -FilePath (Join-Path $evidence 'large-import.txt')
} finally {
    $env:SHORTFORGE_FFMPEG_BIN = $previousBin
    if (-not $KeepMedia) {
        [IO.File]::Delete($seed)
        [IO.File]::Delete($source)
    }
}
