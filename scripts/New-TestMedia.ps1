<#
Generates two short synthetic import fixtures. Existing files are checked and
never replaced. Use -FFmpegBin to select another directory with both tools.
#>
[CmdletBinding()]
param(
    [string]$FFmpegBin,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "../artifacts/fixtures/Тест O'Brien" }
if (-not $FFmpegBin) {
    $localBin = Join-Path $PSScriptRoot '../tools/bin'
    if (Test-Path -LiteralPath (Join-Path $localBin 'ffmpeg.exe')) { $FFmpegBin = $localBin }
}
function Find-MediaTool([string]$Name) {
    if ($FFmpegBin) { return (Get-Item -LiteralPath (Join-Path $FFmpegBin $Name)).FullName }
    return (Get-Command $Name -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
}
$encoder = Find-MediaTool 'ffmpeg.exe'
$probe = Find-MediaTool 'ffprobe.exe'
$directory = [IO.Directory]::CreateDirectory($OutputDirectory).FullName

function Test-Fixture($Path, $Fixture) {
    $json = & $probe -v error -show_streams -show_format -of json -i $Path
    if ($LASTEXITCODE -ne 0) { throw "ffprobe could not read $Path" }
    $metadata = $json -join [Environment]::NewLine | ConvertFrom-Json
    $video = @($metadata.streams | Where-Object codec_type -eq 'video')
    $audio = @($metadata.streams | Where-Object codec_type -eq 'audio')
    $duration = [double]::Parse($metadata.format.duration, [Globalization.CultureInfo]::InvariantCulture)
    if ($Fixture.Container -notin ($metadata.format.format_name -split ',') -or
        $video.Count -ne 1 -or $video[0].codec_name -ne 'h264' -or
        $video[0].width -ne $Fixture.Width -or $video[0].height -ne $Fixture.Height -or
        $video[0].avg_frame_rate -ne "$($Fixture.Rate)/1" -or
        $video[0].pix_fmt -ne 'yuv420p' -or
        $audio.Count -ne $Fixture.AudioCount -or ($audio.Count -and $audio[0].codec_name -ne 'aac') -or
        [Math]::Abs($duration - 2) -gt 0.05) {
        throw "Unexpected fixture metadata in $Path"
    }
}

$fixtures = @(
    @{ Name = 'цвет и тон.mp4'; Container = 'mp4'; Width = 320; Height = 180; Rate = 30; AudioCount = 1 },
    @{ Name = 'тихий кадр.mkv'; Container = 'matroska'; Width = 160; Height = 120; Rate = 25; AudioCount = 0 }
)
foreach ($fixture in $fixtures) {
    $target = Join-Path $directory $fixture.Name
    if (Test-Path -LiteralPath $target) {
        Test-Fixture $target $fixture
        Write-Output $target
        continue
    }
    $extension = [IO.Path]::GetExtension($target)
    $temporary = Join-Path $directory ("fixture-{0}{1}" -f [Guid]::NewGuid().ToString('N'), $extension)
    try {
        $source = 'testsrc2=size={0}x{1}:rate={2}:duration=2' -f $fixture.Width, $fixture.Height, $fixture.Rate
        $arguments = @('-hide_banner', '-loglevel', 'error', '-nostdin', '-n', '-f', 'lavfi', '-i', $source)
        if ($fixture.AudioCount) {
            $arguments += @('-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=2',
                '-map', '0:v:0', '-map', '1:a:0', '-c:a', 'aac', '-movflags', '+faststart')
        } else { $arguments += @('-map', '0:v:0', '-an') }
        $arguments += @('-c:v', 'libx264', '-threads', '1', '-pix_fmt', 'yuv420p', $temporary)
        & $encoder @arguments
        if ($LASTEXITCODE -ne 0) { throw "FFmpeg failed to generate $($fixture.Name)" }
        Test-Fixture $temporary $fixture
        [IO.File]::Move($temporary, $target)
        Write-Output $target
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}
