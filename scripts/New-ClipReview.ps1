# Build an offline listening sheet from completed Test-Clips runs. Input paths are relative to the current directory.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Cases, [Parameter(Mandatory)][string]$OutputDirectory, [string]$EditorialReview)
$ErrorActionPreference = 'Stop'
$bin = Join-Path (Split-Path $PSScriptRoot) 'tools/bin'
function Seconds([long]$us) { ($us / 1000000.0).ToString('0.000000',[cultureinfo]::InvariantCulture) }
function Html([string]$value) { [Net.WebUtility]::HtmlEncode($value) }

# Create a fresh directory so that reviews can be compared and prior ratings remain usable.
$destination = (New-Item -ItemType Directory -Path $OutputDirectory -ErrorAction Stop).FullName
$intervals = @()
$films = @()
# Length-prefix each UTF-8 field so punctuation and newlines in a title cannot change the identity of another field.
$identity = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($identity, [Text.Encoding]::UTF8, $true)
$writer.Write('ShortForge clip review v1')
$number = 0
foreach ($case in (Get-Content -LiteralPath $Cases -Encoding UTF8 -Raw | ConvertFrom-Json)) {
    $source = (Get-Item -LiteralPath $case.source).FullName
    $audioTrack = [int]$case.audioTrack
    if ($audioTrack -lt 0) { throw 'Invalid audio track' }
    $audioStream = "0:a:$audioTrack"
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ($sourceHash -in $films.sha256) { throw 'Duplicate source in review corpus' }
    $films += [ordered]@{ name=[string]$case.name; sha256=$sourceHash; audioTrack=$audioTrack }
    $writer.Write($sourceHash)
    $writer.Write($audioTrack)
    $writer.Write([string]$case.name)
    $groups = [ordered]@{}
    $baseline = $null
    foreach ($mode in 'structure','semantic') {
        $selection = Get-Content -LiteralPath (Join-Path $case.run ($mode + '.json')) -Encoding UTF8 -Raw | ConvertFrom-Json
        if ($selection.Search.Mode -cne $mode -or $selection.Clips -isnot [array]) { throw "Invalid selection in $mode.json" }
        $search = $selection.Search
        foreach ($field in 'MinSeconds','MaxSeconds','Count') {
            if ($search.$field -isnot [int] -and $search.$field -isnot [long]) { throw "Invalid $field in $mode.json" }
        }
        if ($search.MinSeconds -lt 1 -or $search.MaxSeconds -gt 180 -or $search.MinSeconds -gt $search.MaxSeconds -or
            $search.Count -lt 1 -or $search.Count -gt 10 -or $selection.Clips.Count -gt $search.Count) { throw "Invalid range or count in $mode.json" }
        if ($mode -eq 'structure') { $baseline = $search }
        elseif ($search.MinSeconds -ne $baseline.MinSeconds -or $search.MaxSeconds -ne $baseline.MaxSeconds -or $search.Count -ne $baseline.Count) {
            throw 'Compare both modes with the same duration range and requested count.'
        }
        $writer.Write($mode)
        $writer.Write([int]$selection.Search.MinSeconds)
        $writer.Write([int]$selection.Search.MaxSeconds)
        $writer.Write([int]$selection.Search.Count)
        $writer.Write([int]$selection.Clips.Count)
        foreach ($clip in $selection.Clips) {
            foreach ($field in 'StartUs','EndUs') {
                if ($clip.$field -isnot [int] -and $clip.$field -isnot [long]) { throw "Invalid $field in $mode.json" }
            }
            $lengthUs = $clip.EndUs - $clip.StartUs
            if ($clip.StartUs -lt 0 -or $lengthUs -lt $search.MinSeconds * 1000000L -or $lengthUs -gt $search.MaxSeconds * 1000000L -or
                [string]::IsNullOrWhiteSpace($clip.Title)) { throw "Invalid candidate in $mode.json" }
            $writer.Write([long]$clip.StartUs)
            $writer.Write([long]$clip.EndUs)
            $writer.Write([string]$clip.Title)
            $key = '{0}-{1}' -f $clip.StartUs,$clip.EndUs
            if (!$groups.Contains($key)) { $groups[$key] = @{ clip=$clip; rows=@() } }
            if ($mode -in $groups[$key].rows.mode) { throw "Duplicate interval in $mode.json" }
            $groups[$key].rows += [ordered]@{ mode=$mode; title=[string]$clip.Title }
        }
    }
    foreach ($entry in $groups.Values) {
        $number++
        $clip = $entry.clip
        $start = Seconds $clip.StartUs
        $end = Seconds $clip.EndUs
        $durationUs = [long]$clip.EndUs - [long]$clip.StartUs
        if ($clip.StartUs -lt 0 -or $durationUs -le 0) { throw 'Invalid review interval' }
        $duration = Seconds $durationUs
        $file = Join-Path $destination "$number.mp4"
        & (Join-Path $bin 'ffmpeg.exe') -v error -nostdin -n -ss $start -i $source -t $duration -map 0:v:0 -map $audioStream -vf scale=480:-2 -c:v libx264 -threads 2 -preset veryfast -crf 25 -c:a aac -movflags +faststart $file
        if ($LASTEXITCODE) { throw "Review video $number failed" }
        $probed = & (Join-Path $bin 'ffprobe.exe') -v error -show_entries format=duration -of csv=p=0 $file
        if ($LASTEXITCODE -or [math]::Abs([double]::Parse($probed,[cultureinfo]::InvariantCulture) - $durationUs/1e6) -gt 0.1) { throw "Review duration differs at $number" }

        # Use original decoded audio on the source timeline. A short silence separates the two cuts.
        $audio = Join-Path $destination "$number.wav"
        $audioArgs = @('-v','error','-nostdin','-n','-i',$source)
        if ($durationUs -gt 6000000) {
            $headEnd = Seconds ($clip.StartUs + 3000000)
            $tailStart = Seconds ($clip.EndUs - 3000000)
            $filter = "[$audioStream]asplit[h][t];[h]atrim=start=${start}:end=${headEnd},asetpts=PTS-STARTPTS,apad=pad_dur=0.5[head];[t]atrim=start=${tailStart}:end=${end},asetpts=PTS-STARTPTS[tail];[head][tail]concat=n=2:v=0:a=1[a]"
        } else {
            $filter = "[$audioStream]atrim=start=${start}:end=${end},asetpts=PTS-STARTPTS[a]"
        }
        & (Join-Path $bin 'ffmpeg.exe') @audioArgs -filter_complex $filter -map '[a]' -c:a pcm_s16le $audio
        if ($LASTEXITCODE) { throw "Edge extraction failed at $number" }
        $audioDuration = & (Join-Path $bin 'ffprobe.exe') -v error -show_entries format=duration -of csv=p=0 $audio
        $expected = if ($durationUs -gt 6000000) { 6.5 } else { $durationUs / 1e6 }
        if ($LASTEXITCODE -or [math]::Abs([double]::Parse($audioDuration,[cultureinfo]::InvariantCulture)-$expected) -gt 0.02) { throw "Edge duration differs at $number" }
        $intervals += [ordered]@{ number=$number; film=$films.Count-1; startUs=[long]$clip.StartUs; endUs=[long]$clip.EndUs; rows=$entry.rows }
    }
}
$writer.Dispose()
$sha = [Security.Cryptography.SHA256]::Create()
try { $batchId = [BitConverter]::ToString($sha.ComputeHash($identity.ToArray())).Replace('-','').ToLowerInvariant() }
finally { $sha.Dispose(); $identity.Dispose() }
$editorial = @()
if ($EditorialReview) {
    $seed = Get-Content -LiteralPath $EditorialReview -Encoding UTF8 -Raw | ConvertFrom-Json
    if ($seed.batchId -cne $batchId) { throw 'Editorial review belongs to a different selection of clips.' }
    $editorial = @($seed.rows)
    $validKeys = @{}
    foreach ($interval in $intervals) {
        foreach ($row in $interval.rows) { $validKeys["$($interval.number)/$($row.mode)"] = $true }
    }
    foreach ($row in $editorial) {
        $key = "$($row.number)/$($row.mode)"
        if (!$validKeys.ContainsKey($key)) { throw "Unknown or duplicate editorial row $key" }
        $validKeys.Remove($key)
        foreach ($field in 'independence','completion','titleMatch') {
            if (($row.$field -isnot [long] -and $row.$field -isnot [int]) -or $row.$field -notin 0,1) { throw "Invalid grade $field for $key" }
        }
        if ($row.note -isnot [string] -or [string]::IsNullOrWhiteSpace($row.note)) { throw "Missing editorial note for $key" }
    }
    if ($validKeys.Count) { throw 'Editorial file does not cover every proposal.' }
}
$data = [ordered]@{ batchId=$batchId; films=$films; intervals=$intervals; editorial=$editorial }
$manifest = $data | ConvertTo-Json -Compress -Depth 8
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'clip-review.html') -Encoding UTF8 -Raw
$page = $template.Replace('__NONCE__',[guid]::NewGuid().ToString('N')).Replace('__BATCH__',(Html $manifest))
$temporaryPage = Join-Path $destination ('index-' + [guid]::NewGuid().ToString('N') + '.tmp')
[IO.File]::WriteAllText($temporaryPage, $page, [Text.UTF8Encoding]::new($false))
[IO.File]::Move($temporaryPage, (Join-Path $destination 'index.html'))
Write-Host "$number unique intervals. Review $batchId"
Write-Host "Listening sheet: $destination\index.html"
