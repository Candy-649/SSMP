# Uploads the zip that installer/pack-nexus.ps1 made to the mod's page on Nexus Mods, as the newest version of the
# file already there, so a release needs no upload by hand.
#
# It talks to the Nexus Mods API (v3, described at https://api.nexusmods.com/openapi.yaml) the way Nexus Mods' own
# upload action does (https://github.com/Nexus-Mods/upload-action), with the uploader's personal API key from
# https://www.nexusmods.com/settings/api-keys. The key is asked for once, in a prompt that does not show it, and is
# kept encrypted for the current Windows user (DPAPI) in %APPDATA%\silksong-coop-mod. It is never printed, never
# written into the repository and never taken on the command line. NEXUSMODS_API_KEY, when set, is used instead.
#
# In order, it:
#   1. takes the zip of the newest version in dist-nexus - pack-nexus.ps1 makes one only for a version that goes to the
#      mod page, as most go to npm alone - and checks that the SSMP.dll in it says that version;
#   2. finds the page (the uploader's only Silksong mod) and the file on it (the only active one), and when there is
#      more than one, asks once and remembers the answer beside the key;
#   3. stops without uploading when the file already has this version or a newer one, so running it again is safe;
#      when the file already has this version, it still goes on to step 6, so a run whose changelog failed can be
#      finished by running it again;
#   4. reads which versions the page's changelog already has;
#   5. uploads the zip, waits for Nexus Mods to check it, and adds it to the file as its newest version in the main
#      category, archiving the previous version and setting the mod's version to match;
#   6. fills the page's changelog: every version that CHANGES.txt marks, from the oldest version on the file up to
#      this one, that the changelog does not have yet, each under its own version, oldest first, with the titles of
#      the CHANGES.txt lines marked with that version. Every version goes to npm, but only some go to Nexus Mods,
#      and the page's changelog should still name the versions in between.
#
# The v3 API can only add to a changelog (adding a version twice lists its lines twice) and cannot read one, so step
# 4 reads it from the v1 API with the same key. Its answer is believed only when every key is a version number and
# one of them is a version on the file. When the read fails or is not believed, step 6 adds only the versions newer
# than any on the file, which cannot be on the page yet, and leaves the older gaps unchecked.
#
# With -ChangelogsOnly it uploads nothing and does only steps 2, 4 and 6, filling the changelog up to the newest
# version already on the file (never past it: the page should not describe a version it does not offer). That reads
# only the version of the newest zip, and CHANGES.txt.
#
# It only adds versions to a file that exists; the page's first file goes up on the website. Uploading is public, so
# it is run by the page's owner, after npm publish, never by a build.
#
# Console output is English for the same reason install.ps1's is: Windows PowerShell 5.1 reads a UTF-8 file without
# a BOM as ANSI and turns anything else into mojibake. The Chinese texts (the file description, the changelog) are
# read from UTF-8 files at run time instead.

[CmdletBinding()]
param(
    # Where pack-nexus.ps1 writes the zips (default ..\dist-nexus). The zip of the newest version there is uploaded.
    [string] $ZipDir,

    # Shown under the file on the page's Files tab, for each version this script adds (default
    # nexus-file-description.txt beside this script).
    [string] $DescriptionFile,

    # Do every check and show what would be uploaded and added to the changelog, but upload and add nothing.
    [switch] $DryRun,

    # Add the version and leave the changelog as it is.
    [switch] $NoChangelog,

    # Upload nothing; only add to the changelog the versions it lacks, up to the newest version already on the file.
    # Reads only the version of the newest zip, and CHANGES.txt.
    [switch] $ChangelogsOnly,

    # Ask again which mod and file take the upload, instead of using the remembered answer.
    [switch] $Choose,

    # Ask for the API key again and replace the saved one (for a key that was revoked or mistyped).
    [switch] $NewKey
)

$ErrorActionPreference = 'Stop'

# Set here rather than in param(): Windows PowerShell 5.1 leaves $PSScriptRoot empty in parameter defaults under -File
if (-not $ZipDir) { $ZipDir = Join-Path $PSScriptRoot '..\dist-nexus' }
if (-not $DescriptionFile) { $DescriptionFile = Join-Path $PSScriptRoot 'nexus-file-description.txt' }

$PackageName = 'silksong-coop-mod'
# NEXUSMODS_API_BASE is the same override Nexus Mods' upload action reads; it exists for testing against a stand-in.
$ApiBase = if ($env:NEXUSMODS_API_BASE) { $env:NEXUSMODS_API_BASE.TrimEnd('/') } else { 'https://api.nexusmods.com/v3' }
# The v1 API, which can read a mod's changelog, beside v3 on the same host (so a stand-in serves both)
$V1Base = if ($ApiBase -match '/v3$') { $ApiBase -replace '/v3$', '/v1' } else { $null }
$UserAgent = "$PackageName-upload"
$SettingsDir = Join-Path $env:APPDATA $PackageName
$KeyFile = Join-Path $SettingsDir 'nexus-api-key.txt'
$ChoiceFile = Join-Path $SettingsDir 'nexus-upload.json'

# What "create a mod file version" accepts for the name and the version
$NamePattern = "^[a-zA-Z0-9 _'().-]{1,50}$"
$VersionPattern = '^[a-zA-Z0-9.-]{1,50}$'

# The zip is read into memory whole. It is about 4 MB, so anything near this size is the wrong file.
$MaxZipBytes = 100MB

# The list of changes that the changelog is made from, one line each, "* [title](version)" in full-width brackets. It
# stays out of the packages: the install notes say only how to install and play (USER 11-10 "只留安装吧").
$ChangesFile = Join-Path $PSScriptRoot 'CHANGES.txt'

# How long Nexus Mods may take to check an upload before the script gives up waiting
$CheckTimeout = [TimeSpan]::FromMinutes(15)

function Write-Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Note($text) { Write-Host "    $text" }

# --- HTTP ---------------------------------------------------------------------------------------------------------
# HttpClient rather than Invoke-RestMethod: it behaves the same in Windows PowerShell 5.1 and PowerShell 7, sets the
# exact headers asked for, and hands back error bodies without throwing.

Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor
    [Net.SecurityProtocolType]::Tls12
$Http = New-Object System.Net.Http.HttpClient
$Http.Timeout = [TimeSpan]::FromMinutes(10)
# Send bodies straight away instead of asking the server first (on by default in Windows PowerShell 5.1)
$Http.DefaultRequestHeaders.ExpectContinue = $false

# JSON with everything outside ASCII written as \u escapes, so no layer's idea of the encoding can change the text
function ConvertTo-AsciiJson($value) {
    $json = ConvertTo-Json $value -Depth 6 -Compress
    return [regex]::Replace($json, '[^\x00-\x7F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
}

# Set when a request got no answer at all (no connection, a broken one, a timeout), as against an error answer
$NoAnswer = $false

function Send-Request([System.Net.Http.HttpRequestMessage] $request) {
    try {
        $response = $Http.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    } catch {
        $script:NoAnswer = $true
        # The path only: a storage URL's query is its signature. The base exception names the real cause, which
        # Windows PowerShell 5.1 hides behind "An error occurred while sending the request".
        $what = "$($request.Method) $($request.RequestUri.AbsolutePath)"
        throw "No answer to $($what): $($_.Exception.GetBaseException().Message)"
    }
    return @{ Response = $response; Text = $text }
}

function Get-ProblemText([string] $text) {
    try {
        $problem = $text | ConvertFrom-Json
        $parts = @($problem.title, $problem.detail) | Where-Object { $_ }
        if ($parts) { return ($parts -join ' - ') }
    } catch { }
    if ($text.Length -gt 300) { return $text.Substring(0, 300) + '...' }
    return $text
}

function Invoke-Nexus([string] $Method, [string] $Path, $Body, [string] $Key, [string] $Base = $ApiBase) {
    $request = New-Object System.Net.Http.HttpRequestMessage(
        (New-Object System.Net.Http.HttpMethod $Method), "$Base$Path")
    $request.Headers.TryAddWithoutValidation('apikey', $Key) | Out-Null
    $request.Headers.TryAddWithoutValidation('User-Agent', $UserAgent) | Out-Null
    $request.Headers.TryAddWithoutValidation('Accept', 'application/json') | Out-Null
    if ($null -ne $Body) {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-AsciiJson $Body))
        $request.Content = New-Object System.Net.Http.ByteArrayContent(, $bytes)
        $request.Content.Headers.ContentType =
            New-Object System.Net.Http.Headers.MediaTypeHeaderValue 'application/json'
    }
    $answer = Send-Request $request
    $code = [int]$answer.Response.StatusCode
    if (-not $answer.Response.IsSuccessStatusCode) {
        $why = Get-ProblemText $answer.Text
        if ($code -eq 401) {
            throw ("Nexus Mods refused the API key ($Method $Path answered 401: $why). Make a new key at`n" +
                   "      https://www.nexusmods.com/settings/api-keys and run this again with -NewKey.")
        }
        throw "$Method $Path answered $code $($answer.Response.ReasonPhrase): $why"
    }
    if ([string]::IsNullOrWhiteSpace($answer.Text)) { return $null }
    return $answer.Text | ConvertFrom-Json
}

# --- The API key --------------------------------------------------------------------------------------------------

function ConvertFrom-Secure([System.Security.SecureString] $secure) {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Get-ApiKey {
    if ($env:NEXUSMODS_API_KEY) { return $env:NEXUSMODS_API_KEY.Trim() }

    if (-not $NewKey -and (Test-Path $KeyFile)) {
        try {
            $secure = (Get-Content $KeyFile -Raw).Trim() | ConvertTo-SecureString
            return (ConvertFrom-Secure $secure).Trim()
        } catch {
            throw "The saved API key in $KeyFile could not be read. Run this again with -NewKey."
        }
    }

    if (-not [Environment]::UserInteractive -or [Console]::IsInputRedirected) {
        throw "No Nexus Mods API key is saved yet. Run this script in a terminal once, so it can ask for one."
    }
    $lines = @(
        'This needs your Nexus Mods API key once. Make one at',
        '    https://www.nexusmods.com/settings/api-keys',
        'and paste it below. It is not shown, and it is saved encrypted for this Windows user only, in',
        "    $KeyFile"
    )
    Write-Host ""
    foreach ($line in $lines) { Write-Host "  $line" -ForegroundColor Yellow }
    $secure = Read-Host -AsSecureString '  API key'
    $key = (ConvertFrom-Secure $secure).Trim()
    if (-not $key) { throw "No key was pasted. Nothing was saved." }

    # Save only a key Nexus Mods accepts
    Invoke-Nexus GET '/team-mods?page=1&page_size=1' $null $key | Out-Null
    New-Item -ItemType Directory -Force -Path $SettingsDir | Out-Null
    ConvertTo-SecureString $key -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -Path $KeyFile
    Write-Ok "Key accepted and saved."
    return $key
}

# --- The zip ------------------------------------------------------------------------------------------------------

function Get-Sha256Hex([System.IO.Stream] $stream) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('X2') }) }
    finally { $sha.Dispose() }
}

# The zip of the newest version in dist-nexus, which pack-nexus.ps1 makes only for a version that goes to the mod page,
# with the version that the SSMP.dll in it says
function Get-Release {
    $found = $null
    $foundNumber = $null
    if (Test-Path -LiteralPath $ZipDir) {
        foreach ($file in @(Get-ChildItem -LiteralPath $ZipDir -Filter "$PackageName-*.zip" -File)) {
            $text = $file.BaseName.Substring($PackageName.Length + 1)
            $number = $null
            if ($text -notmatch $VersionPattern -or -not [version]::TryParse($text, [ref]$number)) { continue }
            if ($null -eq $foundNumber -or $number -gt $foundNumber) {
                $found = $file
                $foundNumber = $number
            }
        }
    }
    if ($null -eq $found) { throw "There is no $PackageName-<version>.zip in $ZipDir. Run pack-nexus.ps1 first." }
    $version = $found.BaseName.Substring($PackageName.Length + 1)
    $zip = $found.FullName

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $product = $null
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        # Laid out as the game folder (see pack-nexus.ps1)
        $entries = @($archive.Entries)
        $dll = $entries | Where-Object { $_.FullName.Replace('\', '/') -eq 'BepInEx/plugins/SSMP/SSMP.dll' } |
            Select-Object -First 1
        if (-not $dll) { throw "$($found.Name) has no BepInEx/plugins/SSMP/SSMP.dll." }

        # The version of a DLL is read from a file
        $temp = [System.IO.Path]::GetTempFileName()
        try {
            $in = $dll.Open()
            try {
                $out = [System.IO.File]::Create($temp)
                try { $in.CopyTo($out) } finally { $out.Dispose() }
            } finally {
                $in.Dispose()
            }
            $product = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($temp).ProductVersion
        } finally {
            Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        }
    } finally {
        $archive.Dispose()
    }
    if (-not $product -or -not $product.StartsWith("$version.")) {
        throw "The SSMP.dll in $($found.Name) says '$product', not $version. Run pack-nexus.ps1 again."
    }

    $bytes = [System.IO.File]::ReadAllBytes($zip)
    if ($bytes.Length -gt $MaxZipBytes) { throw "$($found.Name) is over 100 MB, which this script does not upload." }
    return @{ Version = $version; Name = $found.Name; Path = $zip; Bytes = $bytes; Product = $product }
}

# The titles of the changes marked with this version, e.g. "* [title](0.4.99)" in full-width brackets
function Get-ChangelogLines([string] $version) {
    $text = [System.IO.File]::ReadAllText($ChangesFile, [System.Text.Encoding]::UTF8)
    $header = '^\* \u3010(?<title>.+?)\u3011\uFF08(?<note>[^\uFF09]*)\uFF09\s*$'
    $marked = '(^|[^0-9.])' + [regex]::Escape($version) + '($|[^0-9])'
    $lines = @()
    foreach ($line in ($text -split "\r?\n")) {
        $match = [regex]::Match($line, $header)
        if (-not $match.Success) { continue }
        $note = $match.Groups['note'].Value
        if ($note -notmatch $marked) { continue }
        if ($note -eq $version) { $lines += $match.Groups['title'].Value }
        else { $lines += $match.Groups['title'].Value + [char]0xFF08 + $note + [char]0xFF09 }
    }
    return , $lines
}

# --- The page and the file ----------------------------------------------------------------------------------------

function Read-Choice {
    if ($Choose -or -not (Test-Path $ChoiceFile)) { return $null }
    try { return Get-Content $ChoiceFile -Raw | ConvertFrom-Json } catch { return $null }
}

function Save-Choice([string] $modId, [string] $fileId) {
    New-Item -ItemType Directory -Force -Path $SettingsDir | Out-Null
    ConvertTo-Json @{ mod_id = $modId; file_id = $fileId } | Set-Content -Path $ChoiceFile -Encoding ASCII
}

function Select-One([string] $what, $items, [scriptblock] $label) {
    if (-not [Environment]::UserInteractive -or [Console]::IsInputRedirected) {
        throw "More than one $what could take the upload. Run this in a terminal once, so it can ask which."
    }
    Write-Host ""
    Write-Host "  More than one $what could take the upload:" -ForegroundColor Yellow
    for ($i = 0; $i -lt $items.Count; $i++) { Write-Host ("    {0}. {1}" -f ($i + 1), (& $label $items[$i])) }
    while ($true) {
        $answer = Read-Host "  Which one (1-$($items.Count))"
        $number = 0
        if ([int]::TryParse($answer, [ref]$number) -and $number -ge 1 -and $number -le $items.Count) {
            return $items[$number - 1]
        }
    }
}

function Get-TeamMods([string] $key) {
    $mods = @()
    $page = 1
    while ($true) {
        $answer = Invoke-Nexus GET "/team-mods?page=$page&page_size=100" $null $key
        $batch = @($answer.data.mods)
        $mods += $batch
        if ($batch.Count -eq 0 -or $mods.Count -ge [int]$answer.meta.total_count) { break }
        $page++
    }
    return , $mods
}

function Find-Target([string] $key) {
    $remembered = Read-Choice
    $picked = $false

    $teamMods = Get-TeamMods $key
    $mod = $null
    if ($remembered) {
        $mod = ($teamMods | Where-Object { $_.mod.id -eq $remembered.mod_id } | Select-Object -First 1).mod
    }
    if (-not $mod) {
        $silksong = @($teamMods | Where-Object { $_.mod.game.domain_name -like '*silksong*' } |
            ForEach-Object { $_.mod })
        if ($silksong.Count -eq 0) {
            $all = ($teamMods | ForEach-Object { "$($_.mod.name) ($($_.mod.game.domain_name))" }) -join ', '
            if (-not $all) { $all = 'none' }
            throw "None of the mods this key can change is a Silksong mod. Mods it can change: $all."
        }
        if ($silksong.Count -eq 1) { $mod = $silksong[0] }
        else {
            $mod = Select-One 'mod' $silksong { param($m) "$($m.name) (mods/$($m.game_scoped_id), $($m.status))" }
            $picked = $true
        }
    }

    $files = @((Invoke-Nexus GET "/mods/$($mod.id)/files" $null $key).data.mod_files | Where-Object { $_.is_active })
    $file = $null
    if ($remembered -and $remembered.mod_id -eq $mod.id) {
        $file = $files | Where-Object { $_.id -eq $remembered.file_id } | Select-Object -First 1
    }
    if (-not $file) {
        if ($files.Count -eq 0) {
            throw ("$($mod.name) has no active file. Upload the first one on the website; this script adds the" +
                   "`n      versions after it.")
        }
        if ($files.Count -eq 1) { $file = $files[0] }
        else {
            $file = Select-One 'file' $files {
                param($f) "$($f.name) ($($f.versions_count) versions, last upload $($f.last_file_uploaded_at))"
            }
            $picked = $true
        }
    }
    if ($picked) { Save-Choice $mod.id $file.id }

    $page = "https://www.nexusmods.com/$($mod.game.domain_name)/mods/$($mod.game_scoped_id)?tab=files"
    return @{ Mod = $mod; File = $file; Page = $page }
}

# --- The upload ---------------------------------------------------------------------------------------------------

# Multipart, as Nexus Mods' upload action does it, even though the zip is one part
function Send-Zip($release, [string] $key) {
    $bytes = $release.Bytes
    $session = @{ filename = $release.Name; size_bytes = $bytes.Length }
    $created = (Invoke-Nexus POST '/uploads/multipart' $session $key).data
    $partSize = [long]$created.part_size_bytes
    $urls = @($created.part_presigned_urls)
    if ($partSize -le 0 -or $urls.Count -lt 1 -or ($urls.Count - 1) * $partSize -ge $bytes.Length -or
        $urls.Count * $partSize -lt $bytes.Length) {
        throw "Nexus Mods asked for $($urls.Count) parts of $partSize bytes for a file of $($bytes.Length) bytes."
    }

    $parts = @()
    for ($i = 0; $i -lt $urls.Count; $i++) {
        $offset = [long]$i * $partSize
        $length = [int][Math]::Min($partSize, $bytes.Length - $offset)
        Write-Note "Part $($i + 1) of $($urls.Count): $length bytes"
        $request = New-Object System.Net.Http.HttpRequestMessage(
            (New-Object System.Net.Http.HttpMethod 'PUT'), [string]$urls[$i])
        $request.Content = New-Object System.Net.Http.ByteArrayContent($bytes, [int]$offset, $length)
        $request.Content.Headers.ContentType =
            New-Object System.Net.Http.Headers.MediaTypeHeaderValue 'application/octet-stream'
        $answer = Send-Request $request
        if (-not $answer.Response.IsSuccessStatusCode) {
            $code = [int]$answer.Response.StatusCode
            throw "Uploading part $($i + 1) answered $($code): $(Get-ProblemText $answer.Text)"
        }
        $values = $null
        if (-not $answer.Response.Headers.TryGetValues('ETag', [ref]$values)) {
            throw "Uploading part $($i + 1) answered without an ETag."
        }
        $etag = (@($values)[0]).Trim('"')
        $parts += "  <Part>`n    <PartNumber>$($i + 1)</PartNumber>`n    <ETag>$etag</ETag>`n  </Part>"
    }

    $xml = [System.Text.Encoding]::UTF8.GetBytes(
        "<CompleteMultipartUpload>`n$($parts -join "`n")`n</CompleteMultipartUpload>")
    $request = New-Object System.Net.Http.HttpRequestMessage(
        (New-Object System.Net.Http.HttpMethod 'POST'), [string]$created.complete_presigned_url)
    $request.Content = New-Object System.Net.Http.ByteArrayContent(, $xml)
    $request.Content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue 'application/xml'
    $answer = Send-Request $request
    # Storage can answer 200 with an <Error> in the body when completing a multipart upload
    if (-not $answer.Response.IsSuccessStatusCode -or $answer.Text -match '<Error>') {
        throw "Completing the upload answered $([int]$answer.Response.StatusCode): $(Get-ProblemText $answer.Text)"
    }

    Invoke-Nexus POST "/uploads/$($created.id)/finalise" $null $key | Out-Null
    return [string]$created.id
}

function Wait-Checked([string] $uploadId, [string] $key) {
    $started = Get-Date
    $delay = 2.0
    while ($true) {
        # The API names two states, created and available. Like Nexus Mods' own action, anything short of available
        # is waited through, in case the beta reports a step in between.
        $state = [string](Invoke-Nexus GET "/uploads/$uploadId" $null $key).data.state
        if ($state -eq 'available') { return }
        $waited = [int]((Get-Date) - $started).TotalSeconds
        if ((Get-Date) - $started -gt $CheckTimeout) {
            throw ("Nexus Mods was still checking the upload after $waited s (state '$state'). Nothing was added" +
                   "`n      to the page; run this again later.")
        }
        Write-Note "Nexus Mods is checking the file ($state, $waited s)"
        Start-Sleep -Milliseconds ([int]($delay * 1000))
        $delay = [Math]::Min($delay * 1.5, 30.0)
    }
}

# --- The changelog ------------------------------------------------------------------------------------------------

# Every version some change marks, as text. A note can name more than one ("0.4.83, 0.4.84 ...").
function Get-MarkedVersions {
    $text = [System.IO.File]::ReadAllText($ChangesFile, [System.Text.Encoding]::UTF8)
    $header = '^\* \u3010(?<title>.+?)\u3011\uFF08(?<note>[^\uFF09]*)\uFF09\s*$'
    $found = @{}
    foreach ($line in ($text -split "\r?\n")) {
        $match = [regex]::Match($line, $header)
        if (-not $match.Success) { continue }
        # Bounded as Get-ChangelogLines bounds a version, so the two agree on what a note marks
        foreach ($number in [regex]::Matches($match.Groups['note'].Value, '(?<![0-9.])[0-9]+(\.[0-9]+){1,3}')) {
            $parsed = $null
            if ([version]::TryParse($number.Value, [ref]$parsed) -and $number.Value -match $VersionPattern) {
                $found[$number.Value] = $true
            }
        }
    }
    return , @($found.Keys)
}

# The versions of a file as [version], oldest first, leaving out a version text that is not a plain number
function Get-VersionNumbers($versions) {
    $numbers = @()
    foreach ($entry in $versions) {
        $parsed = $null
        if ([version]::TryParse([string]$entry.version, [ref]$parsed)) { $numbers += $parsed }
    }
    return , @($numbers | Sort-Object)
}

# The versions the page's changelog already has. The v1 API answers an object with one property per version, each
# a list of lines. Posting can only add, so a wrong reading of the answer would put lines on the public page that
# cannot be taken back: the answer is believed only when every key is a version number and at least one of them is
# a version on the file ($fileNumbers), and anything else counts as a failed read.
function Get-PageChangelogVersions($mod, [string] $key, $fileNumbers) {
    if (-not $V1Base) { throw "The API base $ApiBase does not end in /v3, so where the v1 API is is not known." }
    $domain = [uri]::EscapeDataString([string]$mod.game.domain_name)
    $id = [uri]::EscapeDataString([string]$mod.game_scoped_id)
    $answer = Invoke-Nexus GET "/games/$domain/mods/$id/changelogs.json" $null $key $V1Base
    # An empty answer ([] or {}) names no version on the file either, so it is not believed: this file's page has
    # a changelog, and an answer without it may be one this script misreads
    if ($null -eq $answer -or ($answer -is [array] -and $answer.Count -eq 0)) {
        throw "It named no version that is on the file (the answer was empty)."
    }
    if ($answer -is [array]) { throw "The changelog came back as a list, not as an object keyed by version." }
    if ($answer -isnot [System.Management.Automation.PSCustomObject]) {
        throw "The changelog came back as '$answer', not as an object keyed by version."
    }
    $names = @($answer.PSObject.Properties | ForEach-Object { [string]$_.Name })
    $onFile = $false
    foreach ($name in $names) {
        $parsed = $null
        if (-not [version]::TryParse($name.Trim(), [ref]$parsed)) {
            throw "It named '$name', which is not a version, so the answer is not read as the changelog."
        }
        if (@($fileNumbers | Where-Object { $_ -eq $parsed }).Count) { $onFile = $true }
    }
    if (-not $onFile) {
        throw "It named no version that is on the file, so the answer is not read as this file's changelog."
    }
    return , $names
}

# What to add to the changelog: each version CHANGES.txt marks from $from up to $upTo ([version]s, both included)
# that the page's changelog lacks, oldest first, with its lines. When the changelog cannot be read and $mustRead is
# not set, only the versions after $after (the file's newest before this upload) are taken, since those cannot be
# on the page yet; with $mustRead set it answers $null instead. A version text that is not a plain number is taken
# on its own. $fileNumbers are the file's versions, which the answer must name one of to be believed.
function Get-ChangelogPlan($mod, [string] $key, $from, [string] $upTo, $after, [bool] $mustRead, $fileNumbers) {
    Write-Step "Reading the page's changelog"
    $have = $null
    try {
        $have = Get-PageChangelogVersions $mod $key $fileNumbers
        Write-Ok "It has $($have.Count) version(s)"
    } catch {
        # A read without an answer must not later read as "no answer came to adding the version"
        $script:NoAnswer = $false
        Write-Note "Could not read it: $($_.Exception.Message)"
        if ($mustRead) { return $null }
        if ($after) {
            Write-Note "Older missing versions were not checked: only the versions after $after, the file's newest,"
            Write-Note "go up, as those cannot be on the page yet."
        } else {
            Write-Note "Older missing versions were not checked: only $upTo goes up."
        }
    }

    # Versions the page has, by their text and by their number, so "0.4.99" also finds a key written differently
    $onPage = @{}
    if ($null -ne $have) {
        foreach ($text in $have) {
            $onPage[$text.Trim()] = $true
            $parsed = $null
            if ([version]::TryParse($text.Trim(), [ref]$parsed)) { $onPage[$parsed.ToString()] = $true }
        }
    }

    $last = $null
    if ([version]::TryParse($upTo, [ref]$last)) {
        if ($null -eq $from) { $from = $last }
        $marked = Get-MarkedVersions
        $candidates = @($marked | Where-Object { ([version]$_) -ge $from -and ([version]$_) -le $last } |
            Sort-Object { [version]$_ })
        if ($null -eq $have -and $after) { $candidates = @($candidates | Where-Object { ([version]$_) -gt $after }) }
    } else {
        $candidates = @($upTo)
    }

    $entries = @()
    foreach ($version in $candidates) {
        if ($onPage.ContainsKey($version)) { continue }
        $parsed = $null
        if ([version]::TryParse($version, [ref]$parsed) -and $onPage.ContainsKey($parsed.ToString())) { continue }
        $lines = Get-ChangelogLines $version
        if ($lines.Count) { $entries += @{ Version = $version; Lines = $lines } }
    }

    if (-not ($entries | Where-Object { $_.Version -eq $upTo }) -and -not $onPage.ContainsKey($upTo)) {
        Write-Note "No install note is marked $upTo, so it gets no changelog."
    }
    if ($entries.Count) {
        Write-Note "Missing from it: $(($entries | ForEach-Object { $_.Version }) -join ', ')"
    } elseif ($null -ne $have) {
        Write-Note "It has every version the install notes mark from $from up to $upTo."
    }
    return @{ Entries = $entries }
}

function Show-ChangelogPlan($plan) {
    Write-Note "Changelog:   $($plan.Entries.Count) version(s) to add"
    foreach ($entry in $plan.Entries) {
        Write-Note "  $($entry.Version), $($entry.Lines.Count) line(s):"
        foreach ($line in $entry.Lines) { Write-Note "    $line" }
    }
}

# Each version as its own changelog, oldest first, so the page lists them in order
function Send-Changelogs($mod, $plan, [string] $key) {
    if (-not $plan.Entries.Count) { return }
    Write-Step 'Adding to the changelog'
    foreach ($entry in $plan.Entries) {
        $script:posting = $true
        $body = @{ version = $entry.Version; changelog = ($entry.Lines -join "`n") }
        Invoke-Nexus POST "/mods/$($mod.id)/changelogs" $body $key | Out-Null
        Write-Ok "$($entry.Version): $($entry.Lines.Count) line(s)"
    }
}

# --- Main ---------------------------------------------------------------------------------------------------------

# Dot-sourcing loads the functions only, for testing them one at a time
if ($MyInvocation.InvocationName -eq '.') { return }

$sending = $false
$added = $false
# Set once a changelog has been sent, so a failure after it does not claim that nothing reached the page
$posting = $false
try {
    if ($ChangelogsOnly -and $NoChangelog) { throw "-ChangelogsOnly and -NoChangelog ask for opposite things." }

    if ($ChangelogsOnly) {
        Write-Step 'Reading the changes'
        $release = Get-Release
        if (-not (Test-Path -LiteralPath $ChangesFile)) { throw "There is no $ChangesFile." }
        Write-Ok "CHANGES.txt, up to $($release.Version)"
    } else {
        Write-Step 'Checking the zip'
        $release = Get-Release
        $sizeMb = [Math]::Round($release.Bytes.Length / 1MB, 2)
        Write-Ok "$($release.Name), $sizeMb MB, SSMP.dll $($release.Product)"

        $description = $null
        if (Test-Path $DescriptionFile) {
            $descriptionPath = (Resolve-Path $DescriptionFile).Path
            $description = [System.IO.File]::ReadAllText($descriptionPath, [System.Text.Encoding]::UTF8).Trim()
        }
    }

    Write-Step 'Finding the page'
    $key = Get-ApiKey
    $target = Find-Target $key
    Write-Ok "$($target.Mod.name): file '$($target.File.name)'"
    Write-Note $target.Page

    # Archived versions are still on the file (and in its changelog); removed ones are not
    $versions = @((Invoke-Nexus GET "/mod-files/$($target.File.id)/versions" $null $key).data.versions |
        Where-Object { $_.category -ne 'removed' } |
        Sort-Object { [decimal]::Parse([string]$_.position, [Globalization.CultureInfo]::InvariantCulture) })
    $newest = if ($versions.Count) { $versions[-1] } else { $null }
    $numbers = Get-VersionNumbers $versions
    $oldestNumber = if ($numbers.Count) { $numbers[0] } else { $null }
    $newestNumber = if ($numbers.Count) { $numbers[-1] } else { $null }

    if ($ChangelogsOnly) {
        if ($null -eq $newestNumber) {
            throw "None of the file's versions is a plain version number, so there is nothing to fill up to."
        }
        Write-Note "Versions on the file: $oldestNumber to $newestNumber"
        $plan = Get-ChangelogPlan $target.Mod $key $oldestNumber ($newestNumber.ToString()) $null $true $numbers
        if ($null -eq $plan) {
            throw ("Without the page's changelog it is not known which versions it lacks, so nothing was added to" +
                   " it.")
        }
        if ($DryRun) {
            Write-Step 'Dry run: this is what would be added'
            Show-ChangelogPlan $plan
            Write-Host ""
            Write-Host "  Nothing was added." -ForegroundColor Yellow
            exit 0
        }
        Send-Changelogs $target.Mod $plan $key
        Write-Host ""
        Write-Host "  Done: $($target.Page)" -ForegroundColor Green
        exit 0
    }

    if ($versions | Where-Object { $_.version -eq $release.Version }) {
        Write-Ok "The file already has version $($release.Version), so nothing is uploaded"
        if (-not $NoChangelog) {
            # Finishes an earlier run whose changelog failed. Without the page's changelog nothing here is known to
            # be missing, so nothing is added.
            $plan = Get-ChangelogPlan $target.Mod $key $oldestNumber $release.Version $null $true $numbers
            if ($null -eq $plan) {
                Write-Note "Without it, nothing was added to the changelog."
            } elseif ($DryRun) {
                Write-Step 'Dry run: this is what would be added'
                Show-ChangelogPlan $plan
            } else {
                Send-Changelogs $target.Mod $plan $key
            }
        }
        Write-Host ""
        $what = if ($DryRun) { 'nothing was uploaded or added' } else { 'nothing was uploaded' }
        Write-Host "  The file already has version $($release.Version); $what." -ForegroundColor Green
        exit 0
    }
    if ($newest) {
        Write-Note "Newest version there: $($newest.version)"
        $theirs = $null; $ours = $null
        if ([version]::TryParse([string]$newest.version, [ref]$theirs) -and
            [version]::TryParse($release.Version, [ref]$ours) -and $theirs -gt $ours) {
            throw ("The file's newest version is $($newest.version), newer than $($release.Version). Nothing was" +
                   " uploaded.")
        }
    }

    # The name the page already shows for this file, with a version number in it moved on to this one, if Nexus Mods
    # accepts it for a new version
    $names = @([string]$target.File.name)
    if ($newest) {
        $names = @([string]$newest.name) + $names
        if ($newest.version) {
            $names = @($names | ForEach-Object { $_.Replace([string]$newest.version, $release.Version) })
        }
    }
    $name = $names | Where-Object { $_ -match $NamePattern } | Select-Object -First 1
    if (-not $name) { $name = $PackageName }

    # Planned before the upload, so a dry run shows the same plan; posted only once the version is on the page
    $plan = $null
    if (-not $NoChangelog) {
        $from = $oldestNumber
        $ours = $null
        if ([version]::TryParse($release.Version, [ref]$ours) -and ($null -eq $from -or $ours -lt $from)) {
            $from = $ours
        }
        $plan = Get-ChangelogPlan $target.Mod $key $from $release.Version $newestNumber $false $numbers
    }

    if ($DryRun) {
        Write-Step 'Dry run: this is what would be uploaded'
        Write-Note "Version:     $($release.Version), named '$name', main category, previous version archived"
        Write-Note "Description: $(if ($description) { $description -replace "`r?`n", ' / ' } else { '(none)' })"
        if ($plan) { Show-ChangelogPlan $plan } else { Write-Note "Changelog:   left as it is" }
        Write-Host ""
        Write-Host "  Nothing was uploaded or added." -ForegroundColor Yellow
        exit 0
    }

    Write-Step "Uploading $($release.Name)"
    $uploadId = Send-Zip $release $key
    Wait-Checked $uploadId $key
    Write-Ok "Uploaded and checked"

    Write-Step "Adding it as version $($release.Version)"
    $body = @{
        upload_id = $uploadId
        name = $name
        version = $release.Version
        file_category = 'main'
        archive_existing_file = $true
        update_mod_version = $true
    }
    if ($description) { $body.description = $description }
    $sending = $true
    Invoke-Nexus POST "/mod-files/$($target.File.id)/versions" $body $key | Out-Null
    $added = $true
    Write-Ok "Version $($release.Version) is on the page; the previous one is archived"

    if ($plan) { Send-Changelogs $target.Mod $plan $key }

    Write-Host ""
    Write-Host "  Done: $($target.Page)" -ForegroundColor Green
} catch {
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($added) {
        Write-Host "  The new version is on the page; only its changelog failed. Running this again uploads nothing" `
            -ForegroundColor Yellow
        Write-Host "  and adds what the changelog still lacks, once the page's changelog can be read." `
            -ForegroundColor Yellow
    } elseif ($sending -and $NoAnswer) {
        Write-Host "  No answer came to adding the version, so it may be on the page anyway. Run this again: it" `
            -ForegroundColor Yellow
        Write-Host "  stops at the version if it is there, and adds it if it is not." -ForegroundColor Yellow
    } elseif ($posting) {
        Write-Host "  Only part of the changelog may have been added. Running this again reads the page's" `
            -ForegroundColor Yellow
        Write-Host "  changelog and adds only what it still lacks." -ForegroundColor Yellow
    } else {
        Write-Host "  Nothing was added to the page." -ForegroundColor Yellow
    }
    exit 1
}
