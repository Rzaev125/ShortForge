# F05 on a user-supplied square-pixel phone recording. Use a fresh local library.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Source, [string]$FFmpegBin)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$file = (Get-Item -LiteralPath $Source).FullName
if (-not $FFmpegBin) { $FFmpegBin = Join-Path $root 'tools/bin' }
$bin = (Resolve-Path -LiteralPath $FFmpegBin).Path
$probe = Join-Path $bin 'ffprobe.exe'
$app = (Get-Item -LiteralPath (Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe')).FullName
$dir = (New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/phone-' + [Guid]::NewGuid().ToString('N')))).FullName
Write-Host "Evidence directory: $dir"
$hash = (Get-FileHash -LiteralPath $file).Hash
$result = & $probe -v error -show_entries 'stream=index,codec_type,codec_name,width,height,sample_aspect_ratio,time_base,start_time,duration,color_transfer,sample_rate:stream_disposition=default,attached_pic:stream_side_data=rotation:format=duration' -of json -i $file
if ($LASTEXITCODE) { throw 'Cannot read media.' }
$info = ($result -join [Environment]::NewLine) | ConvertFrom-Json
$v = $info.streams | Where-Object { $_.codec_type -eq 'video' -and !$_.disposition.attached_pic } | Sort-Object { $_.disposition.default } -Descending | Select-Object -First 1
$a = $info.streams | Where-Object codec_type -eq audio | Select-Object -First 1
if (!$v -or !$a) {throw 'Expected video and audio.'}
if ($v.sample_aspect_ratio -and $v.sample_aspect_ratio -notin '1:1','N/A') {throw 'This test expects square pixels.'}
$frames = & $probe -v error -select_streams $v.index -show_frames -show_entries 'frame=best_effort_timestamp' -of json -i $file
if ($LASTEXITCODE) {throw 'Cannot decode frame timestamps.'}
$pts = @(($frames -join [Environment]::NewLine | ConvertFrom-Json).frames.best_effort_timestamp)
if ($pts.Count -lt 2) {throw 'Too few decoded frames.'}
$steps = for ($i=1; $i -lt $pts.Count; $i++) {
    $step = [long]$pts[$i] - [long]$pts[$i-1]
    if ($step -le 0) {throw 'Non-increasing frame timestamps.'}
    $step
}
$rotation = ($v.side_data_list | Where-Object { $null -ne $_.rotation } | Select-Object -First 1).rotation
$sample = @($steps | Select-Object -First 47)
$extrema = $sample | Measure-Object -Minimum -Maximum
$base = $v.time_base.Split('/')
$tickMs = 1000.0 * [int]$base[0] / [int]$base[1]
[ordered]@{width=$v.width; height=$v.height; rotation=[int]$rotation; codec=$v.codec_name; transfer=$v.color_transfer
    sampleFrames=1+$sample.Count; sampleMinMs=$tickMs*$extrema.Minimum; sampleMaxMs=$tickMs*$extrema.Maximum
    duration=$info.format.duration; frames=$pts.Count; timeBase=$v.time_base; frameSteps=@($steps | Group-Object | Select-Object Name,Count)
    videoStart=$v.start_time; videoDuration=$v.duration; audioStart=$a.start_time; audioDuration=$a.duration
    audioCodec=$a.codec_name; sampleRate=$a.sample_rate} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dir 'reference.json') -Encoding utf8
$previous = $env:SHORTFORGE_FFMPEG_BIN
try {
    $env:SHORTFORGE_FFMPEG_BIN = $bin
    foreach ($phase in 'phone','phone-restart') {
        & $app $phase $dir $file | Tee-Object -FilePath (Join-Path $dir "$phase.txt")
        if ($LASTEXITCODE) {throw "$phase failed (exit $LASTEXITCODE)."}
    }
    if ($hash -ne (Get-FileHash -LiteralPath $file).Hash) {throw 'Original file changed.'}
    'Original SHA-256 preserved. See each phase log for playback availability.' | Tee-Object -FilePath (Join-Path $dir 'results.txt')
} finally { $env:SHORTFORGE_FFMPEG_BIN = $previous }
