# F11: distinguish the selected audio by measuring decoded playback in WebView2.
[CmdletBinding()]
param([string]$FFmpegBin)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (-not $FFmpegBin) { $FFmpegBin = Join-Path $root 'tools/bin' }
$bin = (Resolve-Path -LiteralPath $FFmpegBin).Path
$encoder = Join-Path $bin 'ffmpeg.exe'
$probe = Join-Path $bin 'ffprobe.exe'
$app = (Get-Item -LiteralPath (Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe')).FullName
$evidence = (New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/f11-' + [Guid]::NewGuid().ToString('N')))).FullName
Write-Host "Evidence directory: $evidence"
$source = Join-Path $evidence "Three tracks O'Brien.mkv"
$previousBin = $env:SHORTFORGE_FFMPEG_BIN
try {
    $env:SHORTFORGE_FFMPEG_BIN = $bin
    & $encoder -v error -nostdin -n -f lavfi -i 'testsrc2=size=320x180:rate=12' -f lavfi -i 'sine=frequency=440' -f lavfi -i 'sine=frequency=880' -f lavfi -i 'sine=frequency=660' -t 6 -map 1:a -map 0:v -map 2:a -map 3:a -c:v ffv1 -threads 2 -c:a pcm_s16le -metadata:s:a:0 title=Low -metadata:s:a:0 language=rus -metadata:s:a:1 title=High -metadata:s:a:1 language=eng -metadata:s:a:2 title=Middle -disposition:a:0 0 -disposition:a:1 default $source
    if ($LASTEXITCODE) { throw 'Fixture encoding failed.' }
    $hash = (Get-FileHash -LiteralPath $source).Hash
    $json = & $probe -v error -show_streams -show_format -of json $source
    if ($LASTEXITCODE) { throw 'Source probe failed.' }
    $json | Set-Content -LiteralPath (Join-Path $evidence 'source.json') -Encoding utf8
    foreach ($phase in 'tracks','tracks-restart','tracks-offline-restart') {
        if ($phase -eq 'tracks-restart') {
            $movedSource = Join-Path $evidence 'relocated fixture.mkv'
            [IO.File]::Move($source, $movedSource)
            $source = $movedSource
            # Only this new test profile uses the absent tools directory.
            (Join-Path $evidence 'absent-tools') | ConvertTo-Json | Set-Content -Encoding utf8 -LiteralPath (Join-Path $evidence 'data/tools.json')
        }
        $held = $null
        if ($phase -eq 'tracks-offline-restart') {
            $url = [Uri](Get-Content -LiteralPath (Join-Path $evidence 'selected-url.txt') -Raw | ConvertFrom-Json)
            $name = $url.Segments[-1]
            if ($name -notmatch '^[a-f0-9]{32}\.v2\.mp4$') { throw 'Unexpected cache filename.' }
            $cached = Join-Path (Join-Path $evidence 'data/proxies') $name
            $held = Join-Path $evidence 'held-second-proxy.mp4'
            [IO.File]::Move($cached, $held)
        }
        try {
            & $app $phase $evidence $source | Tee-Object -FilePath (Join-Path $evidence "$phase.txt")
            if ($LASTEXITCODE) { throw "$phase failed (exit $LASTEXITCODE)." }
        } finally { if ($held) { [IO.File]::Move($held, $cached) } }
        $files = @(Get-ChildItem -LiteralPath (Join-Path $evidence 'data/proxies') -File)
        if ($files.Count -ne 2 -or @($files | Where-Object Name -notlike '*.v2.mp4').Count) { throw 'Expected two validated cached proxies.' }
        foreach ($file in $files) {
            $codecs = & $probe -v error -show_entries stream=codec_name -of csv=p=0 $file.FullName
            if ($LASTEXITCODE -or ($codecs -join ',') -ne 'h264,aac') { throw 'Invalid proxy stream mapping.' }
        }
        $snapshot = ($files | Sort-Object Name | ForEach-Object { $_.Name + ' ' + $_.LastWriteTimeUtc.Ticks + ' ' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join [Environment]::NewLine
        if ($phase -eq 'tracks') { $before = $snapshot }
        elseif ($before -ne $snapshot) { throw 'Cached files changed after restart.' }
    }
    if ((Get-FileHash -LiteralPath $source).Hash -ne $hash) { throw 'Source changed.' }
    @("PASS F11 multi-track playback and cache reuse", "Source SHA256 $hash", $snapshot) | Set-Content -LiteralPath (Join-Path $evidence 'results.txt') -Encoding utf8
    Write-Host 'PASS F11 multi-track playback and cache reuse.'
} finally { $env:SHORTFORGE_FFMPEG_BIN = $previousBin }
