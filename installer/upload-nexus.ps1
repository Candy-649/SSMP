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
#   1. checks that dist-nexus holds the zip of the version staged in dist-npm, with the very SSMP.dll the npm package
#      holds, so npm and the mod page get one build under one version;
#   2. finds the page (the uploader's only Silksong mod) and the file on it (the only active one), and when there is
#      more than one, asks once and remembers the answer beside the key;
#   3. stops without uploading when the file already has this version or a newer one, so running it again is safe;
#   4. uploads the zip, waits for Nexus Mods to check it, and adds it to the file as its newest version in the main
#      category, archiving the previous version and setting the mod's version to match;
#   5. adds the version's changelog: the titles of the INSTALL.txt entries marked with this version.
#
# It only adds versions to a file that exists; the page's first file goes up on the website. Uploading is public, so
# it is run by the page's owner, after npm publish, never by a build.
#
# Console output is English for the same reason install.ps1's is: Windows PowerShell 5.1 reads a UTF-8 file without
# a BOM as ANSI and turns anything else into mojibake. The Chinese texts (the file description, the changelog) are
# read from UTF-8 files at run time instead.

[CmdletBinding()]
param(
    # The package that pack-npm.ps1 staged (default ..\dist-npm). Its package.json names the version, and its DLL
    # must be the zip's.
    [string] $StageDir,

    # Where pack-nexus.ps1 wrote the zip (default ..\dist-nexus).
    [string] $ZipDir,

    # Shown under the file on the page's Files tab, for each version this script adds (default
    # nexus-file-description.txt beside this script).
    [string] $DescriptionFile,

    # Do every check and show what would be uploaded, but upload nothing.
    [switch] $DryRun,

    # Add the version without a changelog.
    [switch] $NoChangelog,

    # Ask again which mod and file take the upload, instead of using the remembered answer.
    [switch] $Choose,

    # Ask for the API key again and replace the saved one (for a key that was revoked or mistyped).
    [switch] $NewKey
)

$ErrorActionPreference = 'Stop'

# Set here rather than in param(): Windows PowerShell 5.1 leaves $PSScriptRoot empty in parameter defaults under -File
if (-not $StageDir) { $StageDir = Join-Path $PSScriptRoot '..\dist-npm' }
if (-not $ZipDir) { $ZipDir = Join-Path $PSScriptRoot '..\dist-nexus' }
if (-not $DescriptionFile) { $DescriptionFile = Join-Path $PSScriptRoot 'nexus-file-description.txt' }

$PackageName = 'silksong-coop-mod'
# NEXUSMODS_API_BASE is the same override Nexus Mods' upload action reads; it exists for testing against a stand-in.
$ApiBase = if ($env:NEXUSMODS_API_BASE) { $env:NEXUSMODS_API_BASE.TrimEnd('/') } else { 'https://api.nexusmods.com/v3' }
$UserAgent = "$PackageName-upload"
$SettingsDir = Join-Path $env:APPDATA $PackageName
$KeyFile = Join-Path $SettingsDir 'nexus-api-key.txt'
$ChoiceFile = Join-Path $SettingsDir 'nexus-upload.json'

# What "create a mod file version" accepts for the name and the version
$NamePattern = "^[a-zA-Z0-9 _'().-]{1,50}$"
$VersionPattern = '^[a-zA-Z0-9.-]{1,50}$'

# The zip is read into memory whole. It is about 4 MB, so anything near this size is the wrong file.
$MaxZipBytes = 100MB

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

function Invoke-Nexus([string] $Method, [string] $Path, $Body, [string] $Key) {
    $request = New-Object System.Net.Http.HttpRequestMessage(
        (New-Object System.Net.Http.HttpMethod $Method), "$ApiBase$Path")
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

# The version staged for npm, and the zip of that version with the same SSMP.dll
function Get-Release {
    $packageJson = Join-Path $StageDir 'package.json'
    if (-not (Test-Path $packageJson)) { throw "Nothing is staged at $StageDir. Run pack-npm.ps1 first." }
    $version = [string](Get-Content $packageJson -Raw | ConvertFrom-Json).version
    if ($version -notmatch $VersionPattern) { throw "The staged version '$version' is not one Nexus Mods accepts." }

    $stageDll = Join-Path $StageDir 'SSMP\SSMP.dll'
    $product = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($stageDll).ProductVersion
    if (-not $product.StartsWith("$version.")) {
        throw "The staged SSMP.dll says '$product', not $version. Run pack-npm.ps1 again."
    }

    $name = "$PackageName-$version"
    $zip = Join-Path $ZipDir "$name.zip"
    if (-not (Test-Path $zip)) { throw "There is no $name.zip in $ZipDir. Run pack-nexus.ps1 first." }
    $zip = (Resolve-Path $zip).Path

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        # Laid out as the game folder (see pack-nexus.ps1)
        $entry = $archive.Entries |
            Where-Object { $_.FullName.Replace('\', '/') -eq 'BepInEx/plugins/SSMP/SSMP.dll' } |
            Select-Object -First 1
        if (-not $entry) { throw "$name.zip has no BepInEx/plugins/SSMP/SSMP.dll." }
        $stream = $entry.Open()
        try { $zipDllHash = Get-Sha256Hex $stream } finally { $stream.Dispose() }
    } finally {
        $archive.Dispose()
    }
    $stream = [System.IO.File]::OpenRead($stageDll)
    try { $stageDllHash = Get-Sha256Hex $stream } finally { $stream.Dispose() }
    if ($zipDllHash -ne $stageDllHash) {
        throw ("The SSMP.dll in $name.zip is not the one staged for npm, so the two would differ under one" +
               "`n      version. Run pack-npm.ps1 and then pack-nexus.ps1 again.")
    }

    $bytes = [System.IO.File]::ReadAllBytes($zip)
    if ($bytes.Length -gt $MaxZipBytes) { throw "$name.zip is over 100 MB, which this script does not upload." }
    return @{ Version = $version; Name = "$name.zip"; Path = $zip; Bytes = $bytes; Product = $product }
}

# The titles of the install notes marked with this version, e.g. "* [title](0.4.99)" in full-width brackets
function Get-ChangelogLines([string] $version) {
    $notes = Join-Path $StageDir 'INSTALL.txt'
    if (-not (Test-Path $notes)) { return , @() }
    $text = [System.IO.File]::ReadAllText($notes, [System.Text.Encoding]::UTF8)
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

# --- Main ---------------------------------------------------------------------------------------------------------

# Dot-sourcing loads the functions only, for testing them one at a time
if ($MyInvocation.InvocationName -eq '.') { return }

$sending = $false
$added = $false
try {
    Write-Step 'Checking the zip'
    $release = Get-Release
    $sizeMb = [Math]::Round($release.Bytes.Length / 1MB, 2)
    Write-Ok "$($release.Name), $sizeMb MB, the same SSMP.dll ($($release.Product)) as the npm stage"

    $description = $null
    if (Test-Path $DescriptionFile) {
        $descriptionPath = (Resolve-Path $DescriptionFile).Path
        $description = [System.IO.File]::ReadAllText($descriptionPath, [System.Text.Encoding]::UTF8).Trim()
    }
    $changelog = @()
    if (-not $NoChangelog) { $changelog = Get-ChangelogLines $release.Version }

    Write-Step 'Finding the page'
    $key = Get-ApiKey
    $target = Find-Target $key
    Write-Ok "$($target.Mod.name): file '$($target.File.name)'"
    Write-Note $target.Page

    $versions = @((Invoke-Nexus GET "/mod-files/$($target.File.id)/versions" $null $key).data.versions |
        Where-Object { $_.category -ne 'removed' } |
        Sort-Object { [decimal]::Parse([string]$_.position, [Globalization.CultureInfo]::InvariantCulture) })
    $newest = if ($versions.Count) { $versions[-1] } else { $null }
    if ($versions | Where-Object { $_.version -eq $release.Version }) {
        Write-Host ""
        Write-Host "  The file already has version $($release.Version); nothing was uploaded." -ForegroundColor Green
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

    if ($DryRun) {
        Write-Step 'Dry run: this is what would be uploaded'
        Write-Note "Version:     $($release.Version), named '$name', main category, previous version archived"
        Write-Note "Description: $(if ($description) { $description -replace "`r?`n", ' / ' } else { '(none)' })"
        Write-Note "Changelog:   $($changelog.Count) line(s)"
        foreach ($line in $changelog) { Write-Note "  $line" }
        Write-Host ""
        Write-Host "  Nothing was uploaded." -ForegroundColor Yellow
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

    if ($changelog.Count) {
        Write-Step 'Adding the changelog'
        $text = $changelog -join "`n"
        Invoke-Nexus POST "/mods/$($target.Mod.id)/changelogs" @{ version = $release.Version; changelog = $text } $key |
            Out-Null
        Write-Ok "$($changelog.Count) line(s)"
    } elseif (-not $NoChangelog) {
        Write-Note "No install note is marked $($release.Version), so no changelog was added."
    }

    Write-Host ""
    Write-Host "  Done: $($target.Page)" -ForegroundColor Green
} catch {
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($added) {
        Write-Host "  The new version is on the page; only its changelog failed. Add that on the website if it" `
            -ForegroundColor Yellow
        Write-Host "  matters: running this again stops at the version that is already there." -ForegroundColor Yellow
    } elseif ($sending -and $NoAnswer) {
        Write-Host "  No answer came to adding the version, so it may be on the page anyway. Run this again: it" `
            -ForegroundColor Yellow
        Write-Host "  stops at the version if it is there, and adds it if it is not." -ForegroundColor Yellow
    } else {
        Write-Host "  Nothing was added to the page." -ForegroundColor Yellow
    }
    exit 1
}
