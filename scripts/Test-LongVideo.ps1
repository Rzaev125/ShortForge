# F06: a 30-minute local video, an isolated app profile and real proxy encoding.
[CmdletBinding()]
param([string]$FFmpegBin)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (-not $FFmpegBin) { $FFmpegBin = Join-Path $root 'tools/bin' }
$bin = (Resolve-Path -LiteralPath $FFmpegBin).Path
$encoder = (Get-Item -LiteralPath (Join-Path $bin 'ffmpeg.exe')).FullName
$probe = (Get-Item -LiteralPath (Join-Path $bin 'ffprobe.exe')).FullName
$app = (Get-Item -LiteralPath (Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe')).FullName
$evidence = (New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/f06-' + [Guid]::NewGuid().ToString('N')))).FullName
$seed = Join-Path $evidence 'seed.mkv'
$source = Join-Path $evidence "30 minutes O'Brien.mkv"
Write-Host "Evidence directory: $evidence"

function Get-VideoInfo($path, $label) {
    $json = & $probe -v error -show_entries 'stream=codec_type,codec_name,width,height:format=duration,size' -of json $path
    if ($LASTEXITCODE) { throw "ffprobe failed: $label" }
    $json | Set-Content -LiteralPath (Join-Path $evidence "$label.json") -Encoding UTF8
    $media = $json -join [Environment]::NewLine | ConvertFrom-Json
    if ([Math]::Abs([decimal]::Parse($media.format.duration, [cultureinfo]::InvariantCulture) - 1800) -gt 0.25 -or
        $media.streams.Count -ne 2) { throw "Unexpected timeline or tracks: $label" }
    return $media
}

function Measure-Phase($phase) {
    $stdout = Join-Path $evidence "$phase.txt"
    $stderr = Join-Path $evidence "$phase-errors.txt"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $start = [Diagnostics.ProcessStartInfo]::new($app, "$phase `"$evidence`" `"$source`"")
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $run = [Diagnostics.Process]::Start($start)
    $log = $run.StandardOutput.ReadToEndAsync()
    $errors = $run.StandardError.ReadToEndAsync()
    $samples = [Collections.Generic.List[object]]::new()
    try {
        while (-not $run.HasExited) {
            $tree = [Collections.Generic.HashSet[int]]::new()
            [void]$tree.Add($run.Id)
            $processes = @(Get-CimInstance Win32_Process -Filter "Name='ShortForge.WindowsSmoke.exe' OR Name='msedgewebview2.exe' OR Name='ffmpeg.exe' OR Name='ffprobe.exe'" -Property ProcessId,ParentProcessId,WorkingSetSize,Name)
            do {
                $previous = $tree.Count
                foreach ($p in $processes) {
                    if ($tree.Contains($p.ParentProcessId)) { [void]$tree.Add($p.ProcessId) }
                }
            } while ($previous -ne $tree.Count)
            $owned = @($processes | Where-Object { $tree.Contains($_.ProcessId) })
            if ($owned.Count) {
                $vram = $null
                $gpu = Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory -Property Name,DedicatedUsage -ErrorAction SilentlyContinue
                $matched = @($gpu | Where-Object { $_.Name -match '^pid_(\d+)_' -and $tree.Contains([int]$Matches[1]) })
                if ($matched.Count) { $vram = [long](($matched | Measure-Object DedicatedUsage -Sum).Sum) }
                $samples.Add([pscustomobject]@{
                    elapsedMs = $watch.ElapsedMilliseconds
                    workingSetBytes = [long](($owned | Measure-Object WorkingSetSize -Sum).Sum)
                    encoderWorkingSetBytes = [long](($owned | Where-Object Name -eq 'ffmpeg.exe' | Measure-Object WorkingSetSize -Sum).Sum)
                    dedicatedGpuBytes = $vram
                    processes = ($owned.Name -join ',')
                })
            }
            [void]$run.WaitForExit(500)
        }
        $run.WaitForExit()
        $log.GetAwaiter().GetResult() | Set-Content -LiteralPath $stdout -Encoding UTF8
        $errors.GetAwaiter().GetResult() | Set-Content -LiteralPath $stderr -Encoding UTF8
        Get-Content -LiteralPath $stdout
        if ($run.ExitCode -ne 0) { Get-Content -LiteralPath $stderr; throw "$phase failed ($($run.ExitCode))." }
    } finally {
        $samples | Export-Csv -LiteralPath (Join-Path $evidence "$phase-resources.csv") -NoTypeInformation -Encoding UTF8
        $run.Dispose()
    }
}

$previousBin = $env:SHORTFORGE_FFMPEG_BIN
try {
    $env:SHORTFORGE_FFMPEG_BIN = $bin
    & $encoder -v error -nostdin -n -f lavfi -i 'testsrc2=size=320x180:rate=10' -f lavfi -i 'sine=frequency=440:sample_rate=16000' -t 2 -c:v ffv1 -threads 2 -c:a pcm_s16le $seed
    if ($LASTEXITCODE) { throw 'FFV1 seed encoding failed.' }
    & $encoder -v error -nostdin -n -stream_loop -1 -i $seed -t 1800 -map 0 -c copy $source
    if ($LASTEXITCODE) { throw 'Long source creation failed.' }
    $inputInfo = Get-VideoInfo $source 'source'
    $sourceHash = (Get-FileHash -LiteralPath $source).Hash
    Measure-Phase 'long'
    $cache = Join-Path $evidence 'data/proxies'
    $files = @(Get-ChildItem -LiteralPath $cache -File)
    if ($files.Count -ne 1 -or $files[0].Name -notlike '*.v2.mp4') { throw 'Expected one validated proxy.' }
    $proxy = $files[0]
    $proxyInfo = Get-VideoInfo $proxy.FullName 'proxy'
    if ($proxyInfo.streams[0].codec_name -ne 'h264' -or $proxyInfo.streams[1].codec_name -ne 'aac') { throw 'Invalid proxy codecs.' }
    $hash = (Get-FileHash -LiteralPath $proxy.FullName).Hash
    $mtime = $proxy.LastWriteTimeUtc
    Measure-Phase 'long-restart'
    $files = @(Get-ChildItem -LiteralPath $cache -File)
    if ($files.Count -ne 1 -or $files[0].FullName -ne $proxy.FullName -or $files[0].LastWriteTimeUtc -ne $mtime -or
        $hash -ne (Get-FileHash -LiteralPath $proxy.FullName).Hash -or $sourceHash -ne (Get-FileHash -LiteralPath $source).Hash) { throw 'Source changed or cache was rebuilt.' }
    [pscustomobject]@{
        sourceBytes = [long]$inputInfo.format.size; proxyBytes = $proxy.Length
        thumbnailBytes = (Get-ChildItem (Join-Path $evidence 'data/thumbnails') -File | Measure-Object Length -Sum).Sum
        sqliteBytes = (Get-Item (Join-Path $evidence 'data/library.sqlite3')).Length
        sourceSha256 = $sourceHash; proxySha256 = $hash; cacheReused = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'disk.json') -Encoding utf8
    Write-Host 'PASS F06 import, queue cancellation, retry, playback and cache reuse after restart.'
} finally { $env:SHORTFORGE_FFMPEG_BIN = $previousBin }
