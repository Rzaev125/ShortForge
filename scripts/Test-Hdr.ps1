# F11: compare PQ and HLG with a non-HDR BT.2020 control.
[CmdletBinding()]
param([string]$FFmpegBin)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (-not $FFmpegBin) { $FFmpegBin = Join-Path $root 'tools/bin' }
$bin = (Resolve-Path -LiteralPath $FFmpegBin).Path
$encoder = Join-Path $bin 'ffmpeg.exe'
$probe = Join-Path $bin 'ffprobe.exe'
$app = (Get-Item -LiteralPath (Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe')).FullName
$dir = (New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/hdr-' + [Guid]::NewGuid().ToString('N')))).FullName
Write-Host "Evidence directory: $dir"

function New-Video($label, $name, $videoCodec, $format, $transfer, [switch]$TwoAudio) {
    $file = Join-Path $dir $name
    $audio = @('-an')
    if ($TwoAudio) {
        $audio = @('-f','lavfi','-i','sine=frequency=440','-map','0:v','-map','1:a','-map','1:a','-c:a','pcm_s16le')
    }
    # Equal input code values let the test distinguish the two transfer curves after decoding.
    $filter = "format=yuv420p,geq=lum='16+64*gte(X,W/5)+60*gte(X,2*W/5)+50*gte(X,3*W/5)+45*gte(X,4*W/5)':cb=128:cr=128,format=$format,setparams=range=limited:color_primaries=bt2020:color_trc=${transfer}:colorspace=bt2020nc"
    & $encoder -v error -nostdin -n -f lavfi -i 'testsrc2=size=320x180:rate=24' @audio -t 3 -vf $filter -c:v $videoCodec -threads 2 -pix_fmt $format -color_primaries bt2020 -color_trc $transfer -colorspace bt2020nc $file
    if ($LASTEXITCODE) { throw "Cannot create $label" }
    $json = & $probe -v error -show_entries 'stream=index,codec_name,codec_type,pix_fmt,color_transfer,color_primaries,color_space:format=duration' -of json $file
    if ($LASTEXITCODE) { throw "Cannot probe $label" }
    $json | Set-Content -LiteralPath (Join-Path $dir "$label.json") -Encoding utf8
    $info = ($json -join [Environment]::NewLine) | ConvertFrom-Json
    $v = $info.streams[0]
    if ($v.pix_fmt -ne $format -or $v.color_transfer -ne $transfer -or $v.color_primaries -ne 'bt2020' -or
        ($TwoAudio -and @($info.streams | Where-Object codec_type -eq audio).Count -ne 2)) { throw "Incorrect $label fixture" }
    return $file
}

function Read-LumaSteps($file) {
    foreach ($x in 32,96,160,224,288) {
        $values = & $encoder -v error -nostdin -i $file -frames:v 1 -an -vf "crop=16:16:${x}:64,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-" -f null NUL
        $value = $values | Select-String '^lavfi\.signalstats\.YAVG=(.+)$' | Select-Object -First 1
        if ($LASTEXITCODE -or !$value) { throw 'Cannot measure decoded luma.' }
        [double]::Parse($value.Matches[0].Groups[1].Value, [cultureinfo]::InvariantCulture)
    }
}

$pq = New-Video 'pq' "PQ O'Brien.mkv" 'ffv1' 'yuv420p10le' 'smpte2084' -TwoAudio
$hlg = New-Video 'hlg' 'HLG original.mp4' 'libx264' 'yuv420p' 'arib-std-b67'
$sdr = New-Video 'sdr' 'Wide gamut SDR.mkv' 'ffv1' 'yuv420p10le' 'bt709'
$originals = (Get-FileHash -LiteralPath @($pq,$hlg,$sdr)).Hash -join '/'
$previousBin = $env:SHORTFORGE_FFMPEG_BIN
try {
    $env:SHORTFORGE_FFMPEG_BIN = $bin
    foreach ($phase in 'hdr','hdr-restart') {
        & $app $phase $dir $pq $hlg $sdr | Tee-Object -FilePath (Join-Path $dir "$phase.txt")
        if ($LASTEXITCODE) { throw "$phase failed (exit $LASTEXITCODE)." }
        $copies = @(Get-ChildItem -LiteralPath (Join-Path $dir 'data/proxies') -File)
        $thumbs = @(Get-ChildItem -LiteralPath (Join-Path $dir 'data/thumbnails') -File)
        if ($copies.Count -ne 3 -or $thumbs.Count -ne 1) { throw 'Unexpected derived files.' }
        $samples = @{}
        foreach ($curve in 'PQ','HLG') {
            $name = Get-Content -LiteralPath (Join-Path $dir "$curve-copy.txt")
            $copy = $copies | Where-Object Name -eq $name
            if (!$copy) { throw "Missing $curve preview." }
            $summary = & $probe -v error -show_streams -of json $copy.FullName
            if ($LASTEXITCODE) { throw 'Cannot probe SDR preview.' }
            $streams = (($summary -join [Environment]::NewLine) | ConvertFrom-Json).streams
            $video = $streams | Where-Object codec_type -eq video
            $audio = @($streams | Where-Object codec_type -eq audio)
            if ($video.codec_name -ne 'h264' -or $video.pix_fmt -ne 'yuv420p' -or $video.color_range -ne 'tv' -or
                $video.color_space -ne 'bt709' -or $video.color_primaries -ne 'bt709' -or $video.color_transfer -ne 'bt709' -or
                ($curve -eq 'PQ' -and ($audio.Count -ne 1 -or $audio[0].codec_name -ne 'aac')) -or
                ($curve -eq 'HLG' -and $audio.Count -ne 0)) { throw "Invalid $curve preview." }
            $y = @(Read-LumaSteps $copy.FullName)
            if ([Math]::Abs($y[0]-16) -gt 3 -or $y[1] -le $y[0]+4 -or $y[2] -le $y[1]+4 -or
                $y[3] -le $y[2]+4 -or $y[4] -le $y[3]+4 -or [Math]::Abs($y[4]-235) -gt 2) { throw "Lost grayscale steps in $curve preview: $y" }
            $samples[$curve] = $y
            "$curve decoded Y (input 16/80/140/190/235): $y" | Tee-Object -FilePath (Join-Path $dir "$curve-luma.txt")
        }
        if ([Math]::Abs($samples['PQ'][2]-$samples['HLG'][2]) -lt 8) { throw 'PQ and HLG produced indistinguishable midtones.' }
        $snapshot = ($copies | Sort-Object Name | ForEach-Object {
            $_.Name + ' ' + $_.LastWriteTimeUtc.Ticks + ' ' + (Get-FileHash -LiteralPath $_.FullName).Hash
        }) -join [Environment]::NewLine
        if ($phase -eq 'hdr') { $before = $snapshot }
        elseif ($snapshot -ne $before) { throw 'SDR proxy was rebuilt.' }
    }
    if (((Get-FileHash -LiteralPath @($pq,$hlg,$sdr)).Hash -join '/') -ne $originals) { throw 'Source changed.' }
    @('PASS F11 HDR import, SDR tone mapping, playback and restart', "Originals SHA256 (PQ/HLG/SDR): $originals", "Cached copies: $snapshot") | Tee-Object -FilePath (Join-Path $dir 'results.txt')
} finally { $env:SHORTFORGE_FFMPEG_BIN = $previousBin }
