[CmdletBinding()]
param([Parameter(Mandatory)][string]$Source, [ValidateRange(1,65535)][int]$Port=11434, [switch]$ShortRange)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$file=(Get-Item -LiteralPath $Source).FullName
$output=(New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/clips-'+[Guid]::NewGuid().ToString('N')))).FullName
$previousBin=$env:SHORTFORGE_FFMPEG_BIN
$previousPort=$env:SHORTFORGE_OLLAMA_PORT
$previousRange=$env:SHORTFORGE_TEST_SHORT_RANGE
try {
    $env:SHORTFORGE_FFMPEG_BIN=Join-Path $root 'tools/bin'
    $env:SHORTFORGE_OLLAMA_PORT=[string]$Port
    $env:SHORTFORGE_TEST_SHORT_RANGE=if ($ShortRange) { "1" } else { "0" }
    Write-Host "Evidence: $output"
    & (Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe') clips $output $file | Tee-Object -FilePath (Join-Path $output 'results.txt')
    if ($LASTEXITCODE) { throw 'Clip acceptance failed.' }
} finally {
    $env:SHORTFORGE_FFMPEG_BIN=$previousBin
    $env:SHORTFORGE_OLLAMA_PORT=$previousPort
    $env:SHORTFORGE_TEST_SHORT_RANGE=$previousRange
}
