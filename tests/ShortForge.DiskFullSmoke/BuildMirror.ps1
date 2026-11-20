param([Parameter(Mandatory)][string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$dokan = 'C:\Program Files\Dokan\Dokan Library-2.3.1'
$vs = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools'
$mirror = [IO.File]::ReadAllText((Join-Path $dokan 'sample\mirror\mirror.c')).Replace("`r`n", "`n")
function Replace-Once([string]$before, [string]$after) {
    if ($script:mirror.IndexOf($before) -lt 0 -or $script:mirror.IndexOf($before) -ne $script:mirror.LastIndexOf($before)) {
        throw 'Installed mirror.c differs from the expected 2.3.1 sample.'
    }
    $script:mirror = $script:mirror.Replace($before, $after)
}
Replace-Once '#include "../../dokan/dokan.h"' '#include <dokan/dokan.h>'
Replace-Once '#include "../../dokan/fileinfo.h"' '#include <dokan/fileinfo.h>'
# Only the media write is capped. The queue can create ownership and subtitle metadata and clean up normally.
$limit = @'
static int QuotaExceeded(LPCWSTR name, ULONGLONG end, PDOKAN_FILE_INFO info) {
  LPCWSTR base = wcsrchr(name, L'\\');
  if (base && _wcsicmp(base + 1, L"render.partial") == 0 && end > 65536) {
    WCHAR image[MAX_PATH] = L"";
    DWORD size = MAX_PATH;
    HANDLE proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, info->ProcessId);
    if (proc) {
      QueryFullProcessImageNameW(proc, 0, image, &size);
      CloseHandle(proc);
    }
    LPCWSTR executable = wcsrchr(image, L'\\');
    fprintf(stderr, "STATUS_DISK_FULL render.partial requested_end=%llu pid=%lu image=%ls\n", end, info->ProcessId, executable ? executable + 1 : L"unknown");
    fflush(stderr);
    return 1;
  }
  return 0;
}

'@
Replace-Once 'static NTSTATUS DOKAN_CALLBACK MirrorWriteFile(' ($limit + 'static NTSTATUS DOKAN_CALLBACK MirrorWriteFile(')
Replace-Once '  fileSize = ((UINT64)fileSizeHigh << 32) | fileSizeLow;' @'
  fileSize = ((UINT64)fileSizeHigh << 32) | fileSizeLow;
  if (QuotaExceeded(FileName, (DokanFileInfo->WriteToEndOfFile ? fileSize : (UINT64)Offset) + NumberOfBytesToWrite, DokanFileInfo)) {
    *NumberOfBytesWritten = 0;
    if (opened) CloseHandle(handle);
    return STATUS_DISK_FULL;
  }
'@
foreach ($pair in @(@('MirrorSetEndOfFile','ByteOffset'), @('MirrorSetAllocationSize','AllocSize'))) {
    $signature = "static NTSTATUS DOKAN_CALLBACK $($pair[0])(`n  LPCWSTR FileName, LONGLONG $($pair[1]), PDOKAN_FILE_INFO DokanFileInfo) {"
    Replace-Once $signature ($signature + "`n  if (QuotaExceeded(FileName, $($pair[1]), DokanFileInfo)) return STATUS_DISK_FULL;")
}
Replace-Once 'g_HasSeSecurityPrivilege = AddSeSecurityNamePrivilege();' 'g_HasSeSecurityPrivilege = FALSE; /* ordinary current-user test only */'
$build = [IO.Directory]::CreateDirectory($BuildDirectory).FullName
[IO.File]::WriteAllText((Join-Path $build 'quota.c'), $mirror)
& "$vs\Common7\Tools\Launch-VsDevShell.ps1" -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
Push-Location $build
try {
    & cl.exe /nologo /O1 /MT /DUNICODE /D_UNICODE "/I$dokan\include" quota.c /Fe:quota.exe /link "/LIBPATH:$dokan\lib" dokan2.lib advapi32.lib ntdll.lib user32.lib
    if ($LASTEXITCODE) { throw 'Native fixture build failed.' }
    Copy-Item -LiteralPath "$dokan\dokan2.dll" -Destination $build
} finally { Pop-Location }
