[CmdletBinding()]
param(
    [string]$Interview = 'artifacts/p08-corpus/Piers_Sellers_JSC_2016_interview.mp4',
    [string]$Portrait = 'artifacts/p08-corpus/iss074e0575206.jpg',
    [switch]$FixturesOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$bin = Join-Path $root 'tools/bin'
$runner = Join-Path $root 'tests/ShortForge.WindowsSmoke/bin/Release/net10.0-windows/win-x64/ShortForge.WindowsSmoke.exe'
$output = (New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/smoke/autoframe-' + [Guid]::NewGuid().ToString('N')))).FullName
$log = Join-Path $output 'results.txt'
$main = Join-Path $output "Sellers O'Brien.mp4"
$cases = Join-Path $output 'Controlled cases.mp4'
$previous = $env:SHORTFORGE_FFMPEG_BIN

function New-Video([string[]]$Arguments) {
    & (Join-Path $bin 'ffmpeg.exe') -hide_banner -loglevel error -nostdin -n @Arguments
    if ($LASTEXITCODE) { throw 'Could not prepare the labelled test fixture.' }
}
try {
    if ((Get-FileHash -LiteralPath $Interview).Hash -ne 'FEFC84440931EBBCC32079F275C69016A2E12BD1CE4A460D11A8064EC8FE65DE' -or
        (Get-FileHash -LiteralPath $Portrait).Hash -ne '9081228DBFB3F2DC292087AC455C61A3C3A75D3B66CCAA3F0CC52C622186C1F6') {
        throw 'The acceptance annotations require the specific NASA files listed in docs/P08_ACCEPTANCE.md.'
    }
    Write-Host "Evidence: $output"
    $encode = @('-an','-c:v','libx264','-threads','2','-crf','16','-pix_fmt','yuv420p','-movflags','+faststart')
    New-Video (@('-ss','110','-i',$Interview,'-t','9','-vf','fps=30,scale=640:360,setsar=1') + $encode + $main)
    # Bright slates make true edits unambiguous. The opaque patch covers only the interviewee's face.
    $graph = @(
        '[0:v]trim=duration=1.5,setsar=1[a]',
        '[1:v]scale=960:-2,crop=640:360:(iw-ow)/2:40,setsar=1,format=yuv420p,trim=duration=2.5[b]',
        '[2:v]trim=duration=1,setsar=1[c]',
        "[3:v]fps=30,scale=640:360,drawbox=x=160:y=0:w=220:h=165:color=0x303030:t=fill:enable='gte(t,1)*lt(t,1.5)',setsar=1,trim=duration=4[d]",
        '[4:v]fps=30,scale=640:360,crop=240:360:130:0,pad=640:360:400:0:color=white,setsar=1,trim=duration=2[e]',
        '[a][b][c][d][e]concat=n=5:v=1:a=0[out]'
    ) -join ';'
    New-Video (@('-f','lavfi','-i','color=s=640x360:r=30:c=white','-loop','1','-framerate','30','-i',$Portrait,
        '-f','lavfi','-i','color=s=640x360:r=30:c=white','-ss','111','-i',$Interview,'-ss','111','-i',$Interview,
        '-filter_complex_threads','2','-filter_complex',$graph,'-map','[out]') + $encode + $cases)
    if (!$FixturesOnly) {
        $env:SHORTFORGE_FFMPEG_BIN = $bin
        foreach ($phase in @('auto-motion','auto-restart','auto-cases')) {
            $dir = if ($phase -eq 'auto-cases') { Join-Path $output 'cases' } else { Join-Path $output 'motion' }
            $video = if ($phase -eq 'auto-cases') { $cases } else { $main }
            & $runner $phase $dir $video | Tee-Object -FilePath $log -Append
            if ($LASTEXITCODE) { throw "WPF scenario failed: $phase." }
        }
        & node (Join-Path $PSScriptRoot 'Check-AutoFrame.cjs') $output $bin
        if ($LASTEXITCODE) { throw 'Decoded image or trajectory checks failed.' }
    }
} finally { $env:SHORTFORGE_FFMPEG_BIN = $previous }
