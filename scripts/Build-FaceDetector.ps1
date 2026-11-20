param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$output = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
$programs = [Environment]::GetFolderPath('ProgramFilesX86')
$vswhere = Join-Path $programs 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Build requires Visual Studio Build Tools with Desktop development with C++ (MSVC x64).' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Build requires MSVC x64 from Visual Studio Build Tools.' }
$toolset = (Get-Content -LiteralPath (Join-Path $installation 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$vc = Join-Path $installation "VC/Tools/MSVC/$toolset"
$kits = (Get-ItemProperty -LiteralPath 'HKLM:/SOFTWARE/Microsoft/Windows Kits/Installed Roots').KitsRoot10
$sdk = '10.0.26100.0'
$include = Join-Path $kits "Include/$sdk"
if (!(Test-Path -LiteralPath "$include/cppwinrt/winrt/Windows.Media.FaceAnalysis.h")) {
    throw 'Build requires Windows 11 SDK 10.0.26100.0 (C++/WinRT headers and x64 libraries).'
}
$source = Join-Path (Split-Path $PSScriptRoot) 'src/ShortForge.FaceDetector/main.cpp'
$includes = @("/I$vc/include")
$includes += 'cppwinrt','ucrt','um','shared','winrt' | ForEach-Object { "/I$include/$_" }
& "$vc/bin/Hostx64/x64/cl.exe" /nologo /std:c++17 /O2 /EHsc /MT /utf-8 @includes "/Fo$output/face.obj" "/Fe$output/ShortForge.FaceDetector.exe" $source /link /NOLOGO /INCREMENTAL:NO "/LIBPATH:$vc/lib/x64" "/LIBPATH:$kits/Lib/$sdk/um/x64" "/LIBPATH:$kits/Lib/$sdk/ucrt/x64" WindowsApp.lib
if ($LASTEXITCODE) { throw "Face detector build failed ($LASTEXITCODE)." }
