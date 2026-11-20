[CmdletBinding()]
param(
    [string]$LibraryFile = 'artifacts/smoke/editor-4f3b9603ccf647259bc198a30508bf81/data/library.sqlite3',
    [string]$Source = 'artifacts/asr-corpus/nasa-bennu-8-113.mp4'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$output = (New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/captions-' + [Guid]::NewGuid().ToString('N')))).FullName
$data = New-Item -ItemType Directory -Path (Join-Path $output 'data')
Copy-Item -LiteralPath $LibraryFile -Destination (Join-Path $data.FullName 'library.sqlite3')
$runner = Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe'
$bin = Join-Path $root 'tools/bin'
$log = Join-Path $output 'results.txt'
$previous = $env:SHORTFORGE_FFMPEG_BIN

# The caption is pure green. Compare the decoded glyph shapes and their position
# on the actual picture, allowing small differences between separate H.264 encodes.
function Get-GreenMask([string]$FileName, [string]$Time) {
    $frame = Join-Path $output ($FileName + '-' + $Time + '.png')
    & (Join-Path $bin 'ffmpeg.exe') -hide_banner -loglevel error -nostdin -ss $Time -i (Join-Path $output ($FileName + '.mp4')) -an -frames:v 1 -vf 'scale=360:640' -update 1 $frame
    if ($LASTEXITCODE -or !(Test-Path -LiteralPath $frame)) { throw "Frame decode failed for $FileName." }
    $image = [System.Drawing.Bitmap]::new($frame)
    try {
        $mask = [System.Collections.Generic.HashSet[int]]::new()
        for ($y = 0; $y -lt $image.Height; $y++) {
            for ($x = 0; $x -lt $image.Width; $x++) {
                $p = $image.GetPixel($x, $y)
                if ($p.G -ge 110 -and $p.G -gt (55 + [Math]::Max([int]$p.R, [int]$p.B))) {
                    [void]$mask.Add($y * $image.Width + $x)
                }
            }
        }
        return ,$mask
    } finally { $image.Dispose() }
}

function Get-MaskOverlap($First, $Second) {
    $common = [System.Collections.Generic.HashSet[int]]::new([System.Collections.Generic.IEnumerable[int]]$First)
    $common.IntersectWith($Second)
    $union = $First.Count + $Second.Count - $common.Count
    if (!$union) { throw 'Neither frame contains the chosen caption colour.' }
    return $common.Count / $union
}

try {
    $env:SHORTFORGE_FFMPEG_BIN = $bin
    Write-Host "Evidence: $output"
    & $runner captions $output (Get-Item -LiteralPath $Source).FullName | Tee-Object -FilePath $log
    if ($LASTEXITCODE) { throw 'WPF caption acceptance failed.' }
    foreach ($name in 'first-edit', 'second-edit', 'saved-clip') {
        $media = Join-Path $output ($name + '.mp4')
        $raw = & (Join-Path $bin 'ffprobe.exe') -v error -show_format -show_streams -of json $media
        if ($LASTEXITCODE) { throw "Independent ffprobe failed for $name." }
        $raw | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $output ($name + '-probe.json'))
        $probe = ($raw -join [Environment]::NewLine) | ConvertFrom-Json
        $video = @($probe.streams | Where-Object codec_type -eq 'video')
        $audio = @($probe.streams | Where-Object codec_type -eq 'audio')
        $duration = [double]::Parse($probe.format.duration, [CultureInfo]::InvariantCulture)
        if ($video.Count -ne 1 -or $video[0].codec_name -ne 'h264' -or $video[0].width -ne 1080 -or $video[0].height -ne 1920 -or
            $audio.Count -ne 1 -or $audio[0].codec_name -ne 'aac' -or [Math]::Abs($duration - 1.25) -gt 0.07) {
            throw "Unexpected streams or duration for $name."
        }
        "PASS ffprobe $name H.264 1080x1920 AAC, duration $duration s" | Tee-Object -FilePath $log -Append
    }
    Add-Type -AssemblyName System.Drawing
    $before = Get-GreenMask 'first-edit' '0.600'
    $after = Get-GreenMask 'second-edit' '0.600'
    $outside = Get-GreenMask 'saved-clip' '0.100'
    $final = Get-GreenMask 'saved-clip' '0.600'
    $acrossEdits = Get-MaskOverlap $before $after
    $previewVsSaved = Get-MaskOverlap $after $final
    $report = [ordered]@{
        sampleWidth = 360; sampleHeight = 640; activeSeconds = 0.6; beforeCueSeconds = 0.1
        firstEditPixels = $before.Count; secondEditPixels = $after.Count; savedPixels = $final.Count
        beforeCuePixels = $outside.Count; betweenTextEditsIoU = $acrossEdits; previewVsSavedIoU = $previewVsSaved
    }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'caption-pixels.json') -Encoding UTF8
    if ($before.Count -lt 60 -or $after.Count -lt 60 -or $outside.Count -gt $final.Count * .15 -or
        $acrossEdits -ge .85 -or $previewVsSaved -lt .90) { throw 'Decoded caption masks differ from the expected edit or presentation time.' }
    "PASS text-only edit changes the decoded glyphs (IoU $acrossEdits); preview / saved MP4 IoU $previewVsSaved; green pixels before cue $($outside.Count), during cue $($final.Count)." | Tee-Object -FilePath $log -Append
} finally { $env:SHORTFORGE_FFMPEG_BIN = $previous }
