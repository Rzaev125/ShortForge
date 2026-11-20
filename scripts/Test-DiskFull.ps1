$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$harness = Join-Path $repo 'tests\ShortForge.DiskFullSmoke'
$dokan = 'C:\Program Files\Dokan\Dokan Library-2.3.1'
$driver = sc.exe query Dokan2
if ($LASTEXITCODE -or ($driver -join ' ') -notmatch '4\s+RUNNING') { throw 'Dokan2 must already be running.' }
$work = (New-Item -ItemType Directory -Path (Join-Path $repo ('artifacts\smoke\e08-quota-' + [Guid]::NewGuid().ToString('N')))).FullName
$mount = (New-Item -ItemType Directory -Path (Join-Path $work 'mount')).FullName
$back = (New-Item -ItemType Directory -Path (Join-Path $work 'backing')).FullName
$bin = Join-Path $repo 'tools\bin'
$marker = [guid]::NewGuid().ToString('N')
$log = Join-Path $work 'results.txt'
$nativeLog = Join-Path $work 'quota.log'
function Record($message) { $message | Tee-Object -FilePath $log -Append }
Record "Private evidence $work"
$native = Join-Path $work 'native'
& (Join-Path $harness 'BuildMirror.ps1') -BuildDirectory $native
$project = Join-Path $harness 'ShortForge.DiskFullSmoke.csproj'
& dotnet restore $project --locked-mode --source (Join-Path $repo 'artifacts\nuget') -p:NuGetAudit=false
if ($LASTEXITCODE) { throw 'Offline fixture restore failed. Build the solution first.' }
& dotnet build $project --no-restore -c Release -m:1 -p:UseSharedCompilation=false
if ($LASTEXITCODE) { throw 'Queue fixture build failed.' }
& "$bin\ffmpeg.exe" -hide_banner -v error -nostdin -n -f lavfi -i 'testsrc2=size=640x360:rate=30:duration=2' -c:v libx264 -threads 2 (Join-Path $work 'source.mp4')
if ($LASTEXITCODE) { throw 'Source encoding failed.' }
[IO.File]::WriteAllText((Join-Path $back 'identity.txt'), $marker)
if ((Get-Item -LiteralPath $mount).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or (Get-ChildItem -Force -LiteralPath $mount)) {
    throw 'Expected a new empty private directory.'
}
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class E08Unmount {
    [DllImport(@"C:\Program Files\Dokan\Dokan Library-2.3.1\dokan2.dll", CharSet=CharSet.Unicode)]
    public static extern bool DokanRemoveMountPoint(string mountPoint);
}
'@
$expectedTarget = $null
$process = Start-Process -FilePath (Join-Path $native 'quota.exe') -ArgumentList ('/c /r "'+$back+'" /l "'+$mount+'"') -RedirectStandardError $nativeLog -WindowStyle Hidden -PassThru
try {
    $identity = Join-Path $mount 'identity.txt'
    for ($i=0; $i -lt 100 -and !$process.HasExited -and ![IO.File]::Exists($identity); $i++) { Start-Sleep -Milliseconds 100 }
    if ($process.HasExited -or ![IO.File]::Exists($identity) -or [IO.File]::ReadAllText($identity) -ne $marker) { throw 'Current-session mount unavailable.' }
    $expectedTarget = (Get-Item -LiteralPath $mount -Force).Target
    if ($expectedTarget -notmatch '^Volume\{[A-Fa-f0-9-]{36}\}\\$') { throw 'Unexpected mount target.' }
    & (Join-Path $harness 'bin\Release\net10.0\ShortForge.DiskFullSmoke.exe') $work $bin | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE) { throw 'Queue assertions failed.' }
    $observed = Get-Content -LiteralPath $nativeLog | Where-Object { $_ -like 'STATUS_DISK_FULL render.partial*image=ffmpeg.exe' }
    if (!$observed) { throw 'Filesystem did not issue STATUS_DISK_FULL to the media write.' }
    Record ($observed | Select-Object -First 1)
    $outputs = @(Get-ChildItem -LiteralPath (Join-Path $work 'writable') -Filter '*.mp4' -File -Recurse)
    if ($outputs.Count -ne 1) { throw 'Expected a single successful output.' }
    $probe = & "$bin\ffprobe.exe" -v error -show_entries 'stream=codec_name,width,height:format=duration' -of json $outputs[0].FullName
    if ($LASTEXITCODE) { throw 'Independent probe failed.' }
    $metadata = $probe -join "`n" | ConvertFrom-Json
    if ($metadata.streams[0].codec_name -ne 'h264' -or $metadata.streams[0].width -ne 1080 -or $metadata.streams[0].height -ne 1920 -or [decimal]$metadata.format.duration -ne 2) {
        throw 'Following export metadata mismatch.'
    }
    Record 'PASS fresh ffprobe of following job: H.264 1080x1920, duration 2 s.'
    Record 'PASS failed job has no final MP4 or staging files after the real Windows filesystem error.'
} finally {
    if ($expectedTarget -and !$process.HasExited) { $null = [E08Unmount]::DokanRemoveMountPoint($mount) }
    if (!$process.WaitForExit(3000)) { $process.Kill(); $process.WaitForExit() }
    $entry = Get-Item -LiteralPath $mount -Force -ErrorAction SilentlyContinue
    if ($entry -and $entry.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        if (!$expectedTarget -or $entry.Target -ne $expectedTarget) { throw 'Private mount target changed.' }
        # Killing the owned non-admin mirror can leave its dead volume junction on NTFS.
        # Delete only this validated link, with no traversal into the volume.
        [IO.Directory]::Delete($mount, $false)
    }
    if ((Get-Item -LiteralPath $mount -Force -ErrorAction SilentlyContinue).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Test mount remains.' }
    if ([IO.File]::ReadAllText((Join-Path $back 'identity.txt')) -ne $marker) { throw 'Backing marker missing.' }
    Record 'PASS own filesystem process exited; private mount released, backing marker preserved.'
}
