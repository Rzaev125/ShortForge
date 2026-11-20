# P02 / fixture F05. All inputs and the SQLite profile belong to this run.
[CmdletBinding(DefaultParameterSetName='Standard')]
param([string]$FFmpegBin, [Parameter(ParameterSetName='Aspect')][switch]$Anamorphic,
      [Parameter(ParameterSetName='Repair')][switch]$RepairProxy)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (-not $FFmpegBin) { $FFmpegBin = Join-Path $root 'tools/bin' }
$bin = (Resolve-Path -LiteralPath $FFmpegBin).Path
$encoder = (Get-Item -LiteralPath (Join-Path $bin 'ffmpeg.exe')).FullName
$probe = (Get-Item -LiteralPath (Join-Path $bin 'ffprobe.exe')).FullName
$app = (Get-Item -LiteralPath (Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe')).FullName
$evidence = (New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/f05-' + [Guid]::NewGuid().ToString('N')))).FullName
Write-Host "Evidence directory: $evidence"

function New-RotatedFixture($codec) {
    $seed = Join-Path $evidence "$codec-seed.mov"
    $rotated = Join-Path $evidence "VFR O'Brien $codec.mov"
    $filters = "drawbox=x=160:y=0:w=160:h=180:color=blue:t=fill,drawbox=x=0:y=0:w=160:h=180:color=lime:t=fill:enable='gte(n,25)',settb=1/1000,setpts='40*N+7*mod(N,3)'"
    if ($Anamorphic) { $filters += ',setsar=5/4' }
    & $encoder -v error -nostdin -n -f lavfi -i 'color=red:s=320x180:r=25:d=2' -filter_threads 1 -vf $filters -c:v $codec -threads 2 -bf 0 -fps_mode:v passthrough -enc_time_base:v 1:1000 $seed
    if ($LASTEXITCODE) { throw "VFR encoding failed: $codec" }
    & $encoder -v error -nostdin -n -display_rotation:v:0 90 -i $seed -map 0:v:0 -c copy $rotated
    if ($LASTEXITCODE) { throw 'Rotation matrix remux failed.' }
    return $rotated
}

function Get-Timeline($file, $label) {
    $json = & $probe -v error -select_streams v:0 -show_frames -show_streams -show_format -show_entries 'frame=pts_time,duration_time:stream=width,height,codec_name,time_base,sample_aspect_ratio,display_aspect_ratio:stream_side_data=rotation:format=duration' -of json $file
    if ($LASTEXITCODE) { throw "ffprobe failed: $label" }
    $json | Set-Content -LiteralPath (Join-Path $evidence "$label.json") -Encoding utf8
    return ($json -join [Environment]::NewLine | ConvertFrom-Json)
}

function Compare-Timeline($original, $copy, $label) {
    if ($original.frames.Count -ne 50 -or $copy.frames.Count -ne 50) { throw "Unexpected frame count: $label" }
    $maxUs = 0
    for ($i = 0; $i -lt 50; $i++) {
        # Three successive frame intervals are 47, 47 and 26 ms.
        $expectedUs = 40000 * $i + 7000 * ($i % 3)
        $sourceUs = 1000000 * [decimal]::Parse($original.frames[$i].pts_time, [cultureinfo]::InvariantCulture)
        $copyUs = 1000000 * [decimal]::Parse($copy.frames[$i].pts_time, [cultureinfo]::InvariantCulture)
        if ($sourceUs -ne $expectedUs) { throw "Invalid fixture PTS at frame $i" }
        $maxUs = [Math]::Max([decimal]$maxUs, [Math]::Abs($copyUs - $sourceUs))
    }
    return $maxUs
}

function Get-CurrentProxy {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $evidence 'data/proxies') -Filter '*.v2.mp4' -File)
    if ($files.Count -ne 1) { throw 'Expected one current proxy in the isolated profile.' }
    return $files[0]
}

$previousBin = $env:SHORTFORGE_FFMPEG_BIN
try {
    $env:SHORTFORGE_FFMPEG_BIN = $bin
    $playable = New-RotatedFixture libx264
    $unsupported = New-RotatedFixture qtrle
    $before = (Get-FileHash -LiteralPath @($playable,$unsupported)).Hash -join '/'
    $source = Get-Timeline $unsupported 'original'
    if ($source.streams[0].side_data_list[0].rotation -ne 90 -or
        (Compare-Timeline $source (Get-Timeline $playable 'direct') 'direct') -ne 0) { throw 'Fixture validation failed.' }
    $phases = 'vfr','vfr-upgrade'
    if ($RepairProxy) { $phases = 'vfr','vfr-repair','vfr-repair-restart' }
    $expectedWidth = 180
    if ($Anamorphic) {
        if ($source.streams[0].sample_aspect_ratio -ne '5:4' -or $source.streams[0].display_aspect_ratio -ne '20:9') { throw 'Invalid source aspect.' }
        $phases = 'vfr-aspect','vfr-aspect-restart'
        $expectedWidth = 144
    }
    foreach ($phase in $phases) {
        if ($phase -eq 'vfr-repair') {
            $damaged = Get-CurrentProxy
            # Only the newly generated proxy in this run's unique profile is truncated.
            [IO.File]::WriteAllBytes($damaged.FullName, [byte[]](0,1,2,3))
        }
        if ($phase -eq 'vfr-upgrade') {
            # Recreate the previous release's cached MP4 under its genuine SQLite ID.
            # Only this script's newly created profile is ever modified.
            $first = Get-CurrentProxy
            $legacy = Join-Path $first.DirectoryName ($first.Name.Replace('.v2.mp4','.mp4'))
            & $encoder -v error -nostdin -n -i $unsupported -map 0:v:0 -map_metadata -1 -vf 'setsar=1' -fps_mode:v passthrough -c:v libx264 -threads 2 -preset veryfast -crf 23 -pix_fmt yuv420p $legacy
            if ($LASTEXITCODE) { throw 'Legacy proxy encoding failed.' }
            $legacyError = Compare-Timeline $source (Get-Timeline $legacy 'legacy') 'legacy'
            if ($legacyError -eq 0) { throw 'Expected the old encoder settings to round VFR timestamps.' }
            [IO.File]::Move($first.FullName, (Join-Path $evidence 'first-proxy.mp4'))
            Write-Output "Legacy maximum frame PTS error (us): $legacyError" | Tee-Object -FilePath (Join-Path $evidence 'comparison.txt')
        }
        & $app $phase $evidence $playable $unsupported | Tee-Object -FilePath (Join-Path $evidence "$phase.txt")
        if ($LASTEXITCODE) { throw "Windows VFR check failed: $phase" }
        if ($phase -like 'vfr-repair*') {
            $name = Get-Content -LiteralPath (Join-Path $evidence "$phase-name.txt") -Raw
            if ($name -notmatch '^[a-f0-9]{32}\.v2\.mp4$' -or $name -eq $damaged.Name) { throw 'Expected a new proxy ID.' }
            $file = Join-Path $damaged.DirectoryName $name
            if ($phase -eq 'vfr-repair') {
                $repairedName = $name
                $repairedHash = (Get-FileHash -LiteralPath $file).Hash
                $repairedTime = (Get-Item -LiteralPath $file).LastWriteTimeUtc
            } elseif ($name -ne $repairedName -or (Get-FileHash -LiteralPath $file).Hash -ne $repairedHash -or
                (Get-Item -LiteralPath $file).LastWriteTimeUtc -ne $repairedTime) { throw 'Restart did not reuse the repaired proxy.' }
        } else { $file = (Get-CurrentProxy).FullName }
        $copy = Get-Timeline $file "$phase-proxy"
        $errorUs = Compare-Timeline $source $copy $phase
        $video = $copy.streams[0]
        if ($errorUs -ne 0 -or $copy.format.duration -ne $source.format.duration -or
            $video.width -ne $expectedWidth -or $video.height -ne 320 -or $video.codec_name -ne 'h264' -or
            $video.sample_aspect_ratio -ne '1:1' -or
            @($video.side_data_list | Where-Object { $_.rotation }).Count -ne 0) { throw "Proxy verification failed: $phase" }
        Write-Output "$phase PASS: 50 matching PTS, duration $($copy.format.duration), ${expectedWidth}x320, maximum error $errorUs us." | Tee-Object -Append -FilePath (Join-Path $evidence 'results.txt')
    }
    if ($Anamorphic) {
        $thumbs = @(Get-ChildItem -LiteralPath (Join-Path $evidence 'data/thumbnails') -File)
        if ($thumbs.Count -ne 2) { throw 'Expected two thumbnails.' }
        foreach ($thumb in $thumbs) {
            $geometry = & $probe -v error -show_entries stream=width,height,sample_aspect_ratio,display_aspect_ratio -of csv=p=0 $thumb.FullName
            if ($LASTEXITCODE -or $geometry -ne '81,180,1:1,9:20') { throw "Incorrect thumbnail geometry: $geometry" }
        }
        'Two thumbnails: 81x180, SAR 1:1, DAR 9:20.' | Tee-Object -Append -FilePath (Join-Path $evidence 'results.txt')
    }
    if ($before -ne ((Get-FileHash -LiteralPath @($playable,$unsupported)).Hash -join '/')) { throw 'Original content changed.' }
    'PASS F05 synthetic. Original hashes preserved.' | Tee-Object -Append -FilePath (Join-Path $evidence 'results.txt')
} finally { $env:SHORTFORGE_FFMPEG_BIN = $previousBin }
