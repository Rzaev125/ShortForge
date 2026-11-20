$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
& (Join-Path $PSScriptRoot 'Build.ps1') -Configuration Release
Push-Location $root
try {
    dotnet publish src/ShortForge.Desktop --no-restore --no-build -c Release -o artifacts/publish
    if ($LASTEXITCODE) { throw 'Desktop publish failed' }
    $toolDir = Join-Path $root 'tools/bin'
    if ((Test-Path -LiteralPath "$toolDir/ffmpeg.exe") -and (Test-Path -LiteralPath "$toolDir/ffprobe.exe") -and (Test-Path -LiteralPath "$toolDir/LICENSE")) {
        $destination = New-Item -ItemType Directory -Force -Path 'artifacts/publish/tools'
        foreach ($file in 'ffmpeg.exe','ffprobe.exe','LICENSE') {
            Copy-Item -LiteralPath (Join-Path $toolDir $file) -Destination $destination.FullName
        }
    }
    Write-Host (Join-Path $root 'artifacts/publish/ShortForge.exe')
} finally { Pop-Location }
