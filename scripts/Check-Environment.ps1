<#
Read-only environment diagnostics for ShortForge. Windows PowerShell 5.1+.
No downloads, installations, PATH changes or persistent policy changes.
Use -Media to require FFmpeg/ffprobe. FFmpegBin may point to a local bin folder.
WebView2 detection is advisory; only launching the Windows host confirms availability.
#>
[CmdletBinding()]
param(
    [switch]$Media,
    [string]$FFmpegBin = ''
)

$ErrorActionPreference = 'Continue'
$script:Blockers = 0

function Report {
    param([string]$Level, [string]$Message)
    Write-Host ("[{0}] {1}" -f $Level, $Message)
    if ($Level -eq 'FAIL') { $script:Blockers++ }
}

function Locate-Tool {
    param([string]$Name)
    $found = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($found) { return $found.Source }
    return $null
}

function Read-Version {
    param([string]$Executable, [string[]]$Arguments)
    try {
        $output = @(& $Executable @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) { return $null }
        return ($output -join "`n")
    } catch {
        return $null
    }
}

Write-Host 'ShortForge environment check (read only)'
if ($env:OS -ne 'Windows_NT') {
    Report 'FAIL' 'Run this check in Windows PowerShell. Linux/WSL cannot verify WPF runtime.'
    exit 1
}

if ([Environment]::Is64BitOperatingSystem) {
    Report 'OK' '64-bit Windows detected.'
} else {
    Report 'FAIL' 'This project targets Windows x64.'
}
if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') {
    Report 'WARN' 'ARM64 host detected; this plan targets x64. Verify all native dependencies.'
}
if (-not [Environment]::Is64BitProcess) {
    Report 'WARN' 'This shell is 32-bit. Re-run in 64-bit PowerShell for tool discovery.'
}

$dotnetPath = Locate-Tool 'dotnet.exe'
if (-not $dotnetPath) {
    Report 'FAIL' '.NET SDK not found in PATH. Install .NET 10 SDK x64.'
} else {
    $sdkList = Read-Version -Executable $dotnetPath -Arguments @('--list-sdks')
    if ($sdkList -match '(?m)^10\.0\.\d+\s+\[') {
        Report 'OK' '.NET 10 SDK is installed (check global.json when building).'
        Write-Host $sdkList
    } else {
        Report 'FAIL' '.NET 10 stable SDK not found. A runtime alone is insufficient.'
        if ($sdkList) { Write-Host $sdkList }
    }
}

$nodePath = Locate-Tool 'node.exe'
if (-not $nodePath) {
    Report 'FAIL' 'Node.js not found. Install Node.js 24 LTS x64.'
} else {
    $nodeVersion = Read-Version -Executable $nodePath -Arguments @('--version')
    if ($nodeVersion -match '^v24\.') {
        Report 'OK' ("Node.js {0}" -f $nodeVersion)
    } elseif ($nodeVersion) {
        Report 'WARN' ("Node.js {0}; plan baseline is 24 LTS. Check Vite compatibility before changing it." -f $nodeVersion)
    } else {
        Report 'FAIL' 'Node.js could not be executed.'
    }
}

$npmPath = Locate-Tool 'npm.cmd'
if (-not $npmPath) {
    Report 'FAIL' 'npm.cmd not found. Check the Node.js installation.'
} else {
    $npmVersion = Read-Version -Executable $npmPath -Arguments @('--version')
    if ($npmVersion) { Report 'OK' ("npm {0}" -f $npmVersion) }
    else { Report 'FAIL' 'npm.cmd could not be executed.' }
}

$gitPath = Locate-Tool 'git.exe'
if (-not $gitPath) {
    Report 'FAIL' 'Git not found. Install Git for Windows or correct PATH.'
} else {
    $gitVersion = Read-Version -Executable $gitPath -Arguments @('--version')
    if ($gitVersion) { Report 'OK' $gitVersion }
    else { Report 'FAIL' 'Git could not be executed.' }
}

$webViewRoots = @()
foreach ($baseDir in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
    if ($baseDir) { $webViewRoots += (Join-Path $baseDir 'Microsoft\EdgeWebView\Application') }
}
$webViewFound = $false
foreach ($rootDir in ($webViewRoots | Select-Object -Unique)) {
    if (Test-Path -LiteralPath $rootDir -PathType Container) {
        $versions = Get-ChildItem -LiteralPath $rootDir -Directory -ErrorAction SilentlyContinue
        foreach ($versionDir in $versions) {
            if (Test-Path -LiteralPath (Join-Path $versionDir.FullName 'msedgewebview2.exe')) {
                $webViewFound = $true
                break
            }
        }
    }
}
if ($webViewFound) {
    Report 'OK' 'WebView2 runtime files found. Confirm by launching the WPF host.'
} else {
    Report 'WARN' 'WebView2 not detected in common folders. Verify/install Evergreen Runtime x64.'
}

foreach ($toolName in @('ffmpeg.exe', 'ffprobe.exe')) {
    $mediaPath = $null
    if ($FFmpegBin) {
        $candidate = Join-Path $FFmpegBin $toolName
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $mediaPath = $candidate }
    } else {
        $mediaPath = Locate-Tool $toolName
    }
    if ($mediaPath) {
        $mediaVersion = Read-Version -Executable $mediaPath -Arguments @('-version')
        if ($mediaVersion) {
            Report 'OK' (($mediaVersion -split "`n")[0])
            if ($toolName -eq 'ffmpeg.exe') {
                $filters = Read-Version -Executable $mediaPath -Arguments @('-hide_banner', '-filters')
                if ($filters -match '(?m)\bsubtitles\s+V->V') {
                    Report 'OK' 'FFmpeg subtitles filter found.'
                } else {
                    Report 'WARN' 'subtitles filter not detected; verify libass support before P06.'
                }
            }
        } elseif ($Media) {
            Report 'FAIL' ("{0} could not be executed." -f $toolName)
        } else {
            Report 'WARN' ("{0} exists but could not be executed." -f $toolName)
        }
    } elseif ($Media) {
        Report 'FAIL' ("{0} not found. Use -FFmpegBin or configure its path." -f $toolName)
    } else {
        Report 'WARN' ("{0} not found; needed at P02. UI/core work can start without it." -f $toolName)
    }
}

Report 'INFO' 'ASR models, LLM, GPU encode, free disk and Windows UI behavior require later checks.'
Report 'INFO' 'Docker, Python and CUDA Toolkit are not required for P00-P02.'
if ($script:Blockers -gt 0) {
    Write-Host ("Blocking checks: {0}. See docs/SETUP_WINDOWS.md." -f $script:Blockers)
    exit 1
}
Write-Host 'Baseline checks completed. Warnings still require review; this is not an app test.'
exit 0
