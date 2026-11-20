[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LibraryFile,
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$SilentVideo
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$output=(New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/editor-'+[Guid]::NewGuid().ToString('N')))).FullName
$data=New-Item -ItemType Directory -Path (Join-Path $output 'data')
Copy-Item -LiteralPath $LibraryFile -Destination (Join-Path $data.FullName 'library.sqlite3')
$runner=Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe'
$previous=$env:SHORTFORGE_FFMPEG_BIN
try {
    $env:SHORTFORGE_FFMPEG_BIN=Join-Path $root 'tools/bin'
    Write-Host "Evidence: $output"
    foreach ($phase in 'editor','editor-restart','editor-silent') {
        $folder=if ($phase -eq 'editor-silent') { Join-Path $output 'silent' } else { $output }
        $inputFile=if ($phase -eq 'editor-silent') { $SilentVideo } else { $Source }
        & $runner $phase $folder (Get-Item -LiteralPath $inputFile).FullName | Tee-Object -FilePath (Join-Path $output 'results.txt') -Append
        if ($LASTEXITCODE) { throw "Editor acceptance failed at $phase." }
    }
} finally { $env:SHORTFORGE_FFMPEG_BIN=$previous }
