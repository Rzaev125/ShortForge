<#
Run after Build.ps1 on an interactive Windows desktop. Opens two instances of
the production MainWindow in succession using an isolated SQLite/WebView store.
Screenshot names report outer physical window size. The runner prints the real
monitor DPI and independent WebView zoom used for each effective scaling test.
#>
[CmdletBinding()]
param([string]$FFmpegBin, [ValidateSet('100','125','150')][string]$NativeDpiPercent)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (-not $FFmpegBin) { $FFmpegBin = Join-Path $root 'tools/bin' }
$bin = (Resolve-Path -LiteralPath $FFmpegBin).Path
$app = Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe'
if (-not (Test-Path -LiteralPath $app)) { throw 'Build the Release solution with scripts/Build.ps1 first.' }
$evidence = [IO.Directory]::CreateDirectory((Join-Path $root ("artifacts/smoke/" + [Guid]::NewGuid().ToString('N')))).FullName
Write-Host "Evidence directory: $evidence"
$sourceDir = Join-Path $evidence ("Тест O'Brien " + [IO.Path]::GetFileName($evidence))
$fixtures = @(& (Join-Path $PSScriptRoot 'New-TestMedia.ps1') -FFmpegBin $bin -OutputDirectory $sourceDir)
$mp4 = $fixtures[0]
$unsupported = Join-Path $sourceDir 'проверка прокси.mkv'
& (Join-Path $bin 'ffmpeg.exe') -v error -nostdin -n -i $fixtures[1] -an -c:v ffv1 -threads 1 $unsupported
if ($LASTEXITCODE) { throw 'FFV1 fixture encoding failed.' }
$codec = & (Join-Path $bin 'ffprobe.exe') -v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 $unsupported
if ($LASTEXITCODE -or $codec -ne 'ffv1') { throw 'FFV1 fixture verification failed.' }
$before = (Get-FileHash -LiteralPath @($mp4, $unsupported)).Hash -join '/'
$previousBin = $env:SHORTFORGE_FFMPEG_BIN
try {
    $env:SHORTFORGE_FFMPEG_BIN = $bin
    $dpiArguments = @()
    if ($NativeDpiPercent) { $dpiArguments = @($NativeDpiPercent) }
    foreach ($phase in @('import', 'restart')) {
        if ($phase -eq 'restart') {
            # This FFV1 fixture was generated above inside this run's unique directory.
            $relocated = Join-Path $sourceDir 'перенесённый исходник.mkv'
            [IO.File]::Move($unsupported, $relocated)
            $unsupported = $relocated
        }
        & $app $phase $evidence $mp4 $unsupported @dpiArguments | Tee-Object -FilePath (Join-Path $evidence "$phase.txt")
        if ($LASTEXITCODE) { throw "Windows smoke failed during $phase (exit $LASTEXITCODE)." }
    }
} finally { $env:SHORTFORGE_FFMPEG_BIN = $previousBin }
$after = (Get-FileHash -LiteralPath @($mp4, $unsupported)).Hash -join '/'
if ($before -ne $after) { throw 'Source hashes changed during the import and restart smoke.' }
Write-Host "Windows smoke passed; original hashes preserved. Screenshots in $evidence"
