# Puts the package that installer/pack-npm.ps1 staged on a GitHub Release of the public repository, as a zip with the
# installer and the updater in it, so a player who finds the mod on GitHub gets the same self-updating package that
# npm carries, under the same version, without anyone uploading it by hand.
#
# It talks to the GitHub REST API (https://docs.github.com/en/rest/releases) with the login that git already uses for
# github.com: the token is asked from git's credential helper (Git Credential Manager on Windows), the way git asks for
# it before a push, so there is no second key to make or keep. GITHUB_TOKEN, when set, is used instead. The token is
# never printed, never written anywhere and never put into a URL.
#
# In order, it:
#   1. reads the version from the stage's package.json and the commit from its SSMP.dll, which pack-npm.ps1 stamps
#      with the commit it was built from, so the release's tag points at the very build npm carries;
#   2. zips the stage into dist-github, under one top folder, without package.json and the npm README (INSTALL.txt is
#      the guide), and checks the zip itself: the installer, the updater, the loader and the plugin are in it, and its
#      SSMP.dll is the staged one;
#   3. checks that the login belongs to the repository's owner: the owner has a second GitHub account, and Git
#      Credential Manager has handed out the wrong one before;
#   4. stops without uploading when the release of this version already has the zip, or when a newer version is
#      already released, so running it again is safe;
#   5. makes the release as a draft first, uploads the zip to it and only then publishes it, so a public release never
#      exists without its zip. A draft left by a failed run is picked up and finished by the next run.
#
# Releasing is public, so it is run by the repository's owner, after npm publish, never by a build.
#
# Console output is English for the same reason install.ps1's is: Windows PowerShell 5.1 reads a UTF-8 file without
# a BOM as ANSI and turns anything else into mojibake. The Chinese release notes are read from a UTF-8 file at run
# time instead.

[CmdletBinding()]
param(
    # The package that pack-npm.ps1 staged (default ..\dist-npm). Its package.json names the version, and its DLL
    # names the commit.
    [string] $StageDir,

    # Where the zip is written (default ..\dist-github). Beside the repository and gitignored, like the stage.
    [string] $OutDir,

    # The release's description, with {version} and {changes} filled in (default github-release-notes.md beside this
    # script). A line that starts with {no-changes} is kept, without that mark, only when no install note is marked
    # with the version; otherwise it is left out.
    [string] $NotesFile,

    # The repository that takes the release, as owner/name.
    [string] $Repo = 'Candy-649/SSMP',

    # Make and check the zip and show the release it would make, but touch neither the network nor any login.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

# Set here rather than in param(): Windows PowerShell 5.1 leaves $PSScriptRoot empty in parameter defaults under -File
if (-not $StageDir) { $StageDir = Join-Path $PSScriptRoot '..\dist-npm' }
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot '..\dist-github' }
if (-not $NotesFile) { $NotesFile = Join-Path $PSScriptRoot 'github-release-notes.md' }

$PackageName = 'silksong-coop-mod'
# GITHUB_API_URL is the name GitHub's own runners give the API's address; it exists for testing against a stand-in.
$ApiBase = if ($env:GITHUB_API_URL) { $env:GITHUB_API_URL.TrimEnd('/') } else { 'https://api.github.com' }
$UserAgent = "$PackageName-upload"

# The folder of the stage that holds the loader, BepInEx's release unpacked (see pack-npm.ps1)
$LoaderFolder = 'BepInEx_win_x64_5.4.23.4'

# Files of the stage that are for npm only. README.md is the package's page on npm; INSTALL.txt is the player's guide.
$NpmOnly = @('package.json', 'README.md')

# The zip is read into memory whole. It is about 4 MB, so anything near this size is the wrong file.
$MaxZipBytes = 100MB

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

# The token, once the login is checked, and what to call it when GitHub refuses it
$Token = $null
$TokenSource = $null

function Send-Request([System.Net.Http.HttpRequestMessage] $request) {
    try {
        $response = $Http.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    } catch {
        $script:NoAnswer = $true
        # The base exception names the real cause, which Windows PowerShell 5.1 hides behind "An error occurred while
        # sending the request".
        $what = "$($request.Method) $($request.RequestUri.AbsolutePath)"
        throw "No answer to $($what): $($_.Exception.GetBaseException().Message)"
    }
    return @{ Response = $response; Text = $text }
}

# GitHub answers an error with a message and, for a request it would not take, a list of what was wrong
function Get-ProblemText([string] $text) {
    try {
        $problem = $text | ConvertFrom-Json
        $parts = @($problem.message)
        foreach ($item in @($problem.errors)) {
            if ($null -eq $item) { continue }
            if ($item -is [string]) { $parts += $item }
            elseif ($item.message) { $parts += [string]$item.message }
            else { $parts += (@($item.resource, $item.field, $item.code) | Where-Object { $_ }) -join ' ' }
        }
        $parts = @($parts | Where-Object { $_ })
        if ($parts) { return ($parts -join ' - ') }
    } catch { }
    if ($text.Length -gt 300) { return $text.Substring(0, 300) + '...' }
    return $text
}

# Answers @{ Code; Data } rather than the data alone: PowerShell would unroll a JSON array differently in 5.1 and 7.
# A code listed in $Allow is an answer, not an error, such as 404 for "there is no such release".
function Invoke-GitHub([string] $Method, [string] $Url, $Body, [int[]] $Allow = @(), [byte[]] $Bytes,
                       [string] $ContentType) {
    if ($Url.StartsWith('/')) { $Url = "$ApiBase$Url" }
    $request = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod $Method), $Url)
    $request.Headers.TryAddWithoutValidation('Authorization', "Bearer $Token") | Out-Null
    $request.Headers.TryAddWithoutValidation('Accept', 'application/vnd.github+json') | Out-Null
    $request.Headers.TryAddWithoutValidation('X-GitHub-Api-Version', '2022-11-28') | Out-Null
    $request.Headers.TryAddWithoutValidation('User-Agent', $UserAgent) | Out-Null
    if ($null -ne $Bytes) {
        $request.Content = New-Object System.Net.Http.ByteArrayContent(, $Bytes)
        $request.Content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue $ContentType
    } elseif ($null -ne $Body) {
        $json = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-AsciiJson $Body))
        $request.Content = New-Object System.Net.Http.ByteArrayContent(, $json)
        $request.Content.Headers.ContentType =
            New-Object System.Net.Http.Headers.MediaTypeHeaderValue 'application/json'
    }
    $path = $request.RequestUri.AbsolutePath
    $answer = Send-Request $request
    $code = [int]$answer.Response.StatusCode
    if (-not $answer.Response.IsSuccessStatusCode -and $Allow -notcontains $code) {
        $why = Get-ProblemText $answer.Text
        if ($code -eq 401) {
            throw "GitHub refused the login $TokenSource ($Method $path answered 401: $why).$(Get-SignInHint)"
        }
        throw "$Method $path answered $code $($answer.Response.ReasonPhrase): $why"
    }
    $data = $null
    if ($answer.Response.IsSuccessStatusCode -and -not [string]::IsNullOrWhiteSpace($answer.Text)) {
        $data = $answer.Text | ConvertFrom-Json
    }
    return @{ Code = $code; Data = $data }
}

# --- The login ----------------------------------------------------------------------------------------------------

function Get-Owner { return $Repo.Split('/')[0] }

function Get-SignInHint {
    if ($env:GITHUB_TOKEN) {
        return "`n      Check GITHUB_TOKEN, or clear it to use the login git has."
    }
    # git forgets a login that GitHub refuses on a push and asks for a new one, which this script cannot do
    return "`n      Push once with git, so it asks you to sign in as $(Get-Owner), then run this again."
}

# Runs "git credential <action>" with the given lines on its input, the way git itself asks its credential helper.
# A process of its own rather than a pipe: Windows PowerShell 5.1 turns git's error output into an exception under
# $ErrorActionPreference = 'Stop', and only the child is given GIT_TERMINAL_PROMPT=0.
function Invoke-GitCredential([string] $action, [string[]] $fields) {
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $git) { throw "git is not on PATH, so there is no login to borrow. Set GITHUB_TOKEN instead." }
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $git.Path
    $start.Arguments = "credential $action"
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Never ask for a user name or password in this terminal: a typed password is not a token the API takes. Git
    # Credential Manager's own sign-in window still opens when it has no login.
    $start.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        try {
            $process.StandardInput.Write((($fields + '') -join "`n") + "`n")
            $process.StandardInput.Close()
        } catch { }
        $process.WaitForExit()
        return @{ Code = $process.ExitCode; Out = $out.Result; Err = $err.Result }
    } finally {
        $process.Dispose()
    }
}

function Get-Token {
    if ($env:GITHUB_TOKEN) {
        $script:TokenSource = 'in GITHUB_TOKEN'
        return @{ Token = $env:GITHUB_TOKEN.Trim(); FromGit = $false }
    }
    $owner = Get-Owner
    $script:TokenSource = "that git has for $owner"
    $fields = @('protocol=https', 'host=github.com', "username=$owner")
    $answer = Invoke-GitCredential 'fill' $fields
    $token = $null
    if ($answer.Code -eq 0) {
        foreach ($line in ($answer.Out -split "\r?\n")) {
            if ($line.StartsWith('password=')) { $token = $line.Substring('password='.Length) }
        }
    }
    if (-not $token) {
        # git's own words, which name the cause (no helper, a closed sign-in window) and never hold a password
        $said = (($answer.Err -split "\r?\n") | Where-Object { $_.Trim() } | Select-Object -First 1)
        $why = if ($said) { "`n      git said: $($said.Trim())" } else { '' }
        throw ("git has no GitHub login for $owner.$why`n      Push once with git, so it asks you to sign in, " +
               "then run this again.")
    }
    return @{ Token = $token; FromGit = $true; Fields = $fields }
}

# The login must be the repository owner's. Nothing is changed on GitHub before this passes.
function Confirm-Account($login) {
    $owner = Get-Owner
    $user = (Invoke-GitHub GET '/user').Data
    $name = [string]$user.login
    if ($name -ne $owner) {
        if (-not $name) { $name = '(no name)' }
        throw ("The login $TokenSource belongs to GitHub account '$name', not $owner. Nothing was done." +
               "`n      Sign git in to github.com as $owner, then run this again.")
    }
    # A login that works is kept, as git does after a push, so a sign-in made just now is not asked for again
    if ($login.FromGit) {
        $kept = Invoke-GitCredential 'approve' ($login.Fields + "password=$($login.Token)")
        if ($kept.Code -ne 0) { Write-Note "git did not keep the login (exit $($kept.Code)); it may ask again." }
    }
    return $name
}

# --- The stage and the zip ----------------------------------------------------------------------------------------

function Get-Sha256Hex([System.IO.Stream] $stream) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('X2') }) }
    finally { $sha.Dispose() }
}

function Get-FileSha256([string] $path) {
    $stream = [System.IO.File]::OpenRead($path)
    try { return Get-Sha256Hex $stream } finally { $stream.Dispose() }
}

# The version staged for npm, and the commit its SSMP.dll was built from
function Get-Stage {
    $packageJson = Join-Path $StageDir 'package.json'
    if (-not (Test-Path $packageJson)) { throw "Nothing is staged at $StageDir. Run pack-npm.ps1 first." }
    $script:StageDir = (Resolve-Path $StageDir).Path
    $version = [string](Get-Content (Join-Path $StageDir 'package.json') -Raw | ConvertFrom-Json).version
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "The staged version '$version' is not x.y.z." }

    # pack-npm.ps1 stamps "<version>.<n>+<commit>" into the DLL. The commit is where the release's tag goes.
    $dll = Join-Path $StageDir 'SSMP\SSMP.dll'
    if (-not (Test-Path $dll)) { throw "The stage has no SSMP\SSMP.dll. Run pack-npm.ps1 again." }
    $product = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
    if ($product -notmatch ('^' + [regex]::Escape($version) + '\.[0-9]+\+([0-9a-f]{40})$')) {
        throw "The staged SSMP.dll says '$product', not $version with a commit. Run pack-npm.ps1 again."
    }
    return @{ Version = $version; Sha = $matches[1]; Product = $product; DllHash = Get-FileSha256 $dll }
}

# Every file of the stage but the npm-only ones, as paths relative to the stage with forward slashes
function Get-StagedFiles {
    $files = @()
    foreach ($file in (Get-ChildItem -LiteralPath $StageDir -Recurse -Force -File)) {
        $relative = $file.FullName.Substring($StageDir.TrimEnd('\').Length + 1).Replace('\', '/')
        if ($NpmOnly -contains $relative) { continue }
        $files += $relative
    }
    return , $files
}

# The zip holds one folder named after the version, so unpacking it anywhere gives one tidy folder, as the npm package
# does with its "package" folder. Made from a copy of the stage, since the npm-only files have to stay out.
function New-ReleaseZip($stage) {
    $top = "$PackageName-$($stage.Version)"
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $zip = Join-Path (Resolve-Path $OutDir).Path "$top.zip"
    $staged = Get-StagedFiles

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("upload-github-" + [guid]::NewGuid().ToString('N'))
    $base = Join-Path $root $top
    New-Item -ItemType Directory -Path $base | Out-Null
    try {
        # File by file, so the loader's dot-files come along whatever their attributes
        foreach ($relative in $staged) {
            $target = Join-Path $base $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            Copy-Item -LiteralPath (Join-Path $StageDir $relative) -Destination $target
        }
        if (Test-Path $zip) { Remove-Item $zip }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $base, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true
        )
    } finally {
        Remove-Item $root -Recurse -Force
    }

    # Check the zip itself, not the folder it was made from
    Add-Type -AssemblyName System.IO.Compression
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $outside = @($entries | Where-Object { -not $_.StartsWith("$top/") })
        if ($outside) { throw "Entries outside the folder $top/ in the zip: $($outside -join ', ')" }
        $required = @('INSTALL.bat', 'INSTALL.txt', 'install.ps1', 'UPDATE.bat', 'update.ps1', 'LICENSE',
                      'LICENSE-BouncyCastle.md', "$LoaderFolder/winhttp.dll", "$LoaderFolder/doorstop_config.ini",
                      "$LoaderFolder/.doorstop_version", "$LoaderFolder/BepInEx/core/BepInEx.Preloader.dll",
                      'SSMP/SSMP.dll', 'SSMP/BouncyCastle.Cryptography.dll')
        foreach ($name in $required) {
            if ($entries -notcontains "$top/$name") { throw "Missing from the zip: $top/$name" }
        }
        foreach ($name in $staged) {
            if ($entries -notcontains "$top/$name") { throw "A staged file is missing from the zip: $name" }
        }
        foreach ($name in $NpmOnly) {
            if ($entries -contains "$top/$name") { throw "The zip holds $name, which is for npm only." }
        }
        $entry = $archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq "$top/SSMP/SSMP.dll" } |
            Select-Object -First 1
        $stream = $entry.Open()
        try { $zipDllHash = Get-Sha256Hex $stream } finally { $stream.Dispose() }
        if ($zipDllHash -ne $stage.DllHash) { throw "The SSMP.dll in the zip is not the staged one." }
    } finally {
        $archive.Dispose()
    }

    $bytes = [System.IO.File]::ReadAllBytes($zip)
    if ($bytes.Length -gt $MaxZipBytes) { throw "$top.zip is over 100 MB, which this script does not upload." }
    return @{ Name = "$top.zip"; Path = $zip; Bytes = $bytes; Entries = $entries.Count; Hash = Get-FileSha256 $zip }
}

# --- The notes ----------------------------------------------------------------------------------------------------

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

# The release's description from the template. The fallback for a version without marked notes lives in the template,
# on a line that starts with {no-changes}, so the Chinese text stays out of this file.
function Get-Notes([string] $version, $changelog) {
    if (-not (Test-Path $NotesFile)) { throw "There is no release notes template at $NotesFile." }
    $path = (Resolve-Path $NotesFile).Path
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8).Replace("`r`n", "`n")
    $text = $text.Replace('{version}', $version)
    $lines = @($changelog | ForEach-Object { "- $_" })
    if ($lines.Count) {
        $text = [regex]::Replace($text, '(?m)^\{no-changes\}.*(\n|$)', '')
        $text = $text.Replace('{changes}', ($lines -join "`n"))
    } else {
        $text = [regex]::Replace($text, '(?m)^\{changes\}(\n|$)', '')
        $text = $text.Replace('{changes}', '')
        $text = [regex]::Replace($text, '(?m)^\{no-changes\}', '')
    }
    return $text.Trim()
}

# --- The release --------------------------------------------------------------------------------------------------

# A tag of the form v1.2.3 as a version, or $null
function Get-TagVersion([string] $tag) {
    $parsed = $null
    if ($tag -match '^v([0-9]+\.[0-9]+\.[0-9]+)$' -and [version]::TryParse($matches[1], [ref]$parsed)) {
        return $parsed
    }
    return $null
}

# The asset an earlier run uploaded completely: GitHub leaves a failed upload behind in the state "starter". Newer
# answers also carry the file's SHA256 as "digest", which then has to be this zip's.
function Test-GoodAsset($asset, $zip) {
    if ([string]$asset.state -ne 'uploaded' -or [long]$asset.size -ne $zip.Bytes.Length) { return $false }
    $digest = [string]$asset.digest
    return (-not $digest -or $digest -eq "sha256:$($zip.Hash.ToLowerInvariant())")
}

function Send-Zip($release, $zip) {
    # upload_url is a URI template, "...{?name,label}"; following it rather than building the address lets a
    # stand-in name its own
    $url = ([string]$release.upload_url) -replace '\{[^}]*\}$', ''
    $url = "$($url)?name=$([Uri]::EscapeDataString($zip.Name))"
    $answer = Invoke-GitHub POST $url $null @() $zip.Bytes 'application/zip'
    $asset = $answer.Data
    if ($answer.Code -ne 201 -or -not $asset) { throw "Uploading the zip answered $($answer.Code), not 201." }
    if ([string]$asset.state -ne 'uploaded' -or [long]$asset.size -ne $zip.Bytes.Length) {
        throw ("GitHub has the upload as '$($asset.state)', $($asset.size) bytes, not uploaded with " +
               "$($zip.Bytes.Length) bytes.")
    }
}

# --- Main ---------------------------------------------------------------------------------------------------------

# Dot-sourcing loads the functions only, for testing them one at a time
if ($MyInvocation.InvocationName -eq '.') { return }

$draftId = $null
$publishing = $false
try {
    if ($Repo -notmatch '^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$') { throw "-Repo '$Repo' is not owner/name." }

    Write-Step 'Checking the stage'
    $stage = Get-Stage
    Write-Ok "$PackageName $($stage.Version), SSMP.dll $($stage.Product)"

    Write-Step 'Zipping'
    $zip = New-ReleaseZip $stage
    $sizeMb = [Math]::Round($zip.Bytes.Length / 1MB, 2)
    Write-Ok "$($zip.Entries) entries, $sizeMb MB, the same SSMP.dll as the stage"
    Write-Ok "SHA256: $($zip.Hash)"
    Write-Note $zip.Path

    $changelog = Get-ChangelogLines $stage.Version
    $notes = Get-Notes $stage.Version $changelog
    $tag = "v$($stage.Version)"
    $title = "Silksong Coop Mod $($stage.Version)"

    if ($DryRun) {
        Write-Step 'Dry run: this is what would be released'
        Write-Note "Repository: $Repo"
        Write-Note "Tag:        $tag, at $($stage.Sha)"
        Write-Note "Name:       $title"
        Write-Note "Zip:        $($zip.Path)"
        Write-Note "Changes:    $($changelog.Count) line(s)"
        Write-Note "Notes:"
        foreach ($line in ($notes -split "`n")) { Write-Host "      | $line" }
        Write-Host ""
        Write-Host "  Nothing was sent to GitHub." -ForegroundColor Yellow
        exit 0
    }

    Write-Step 'Checking the login'
    $login = Get-Token
    $Token = $login.Token
    $account = Confirm-Account $login
    Write-Ok "Signed in as $account"

    Write-Step "Looking for release $tag on $Repo"
    $published = (Invoke-GitHub GET "/repos/$Repo/releases/tags/$tag" $null @(404)).Data
    if ($published) {
        if (@($published.assets) | Where-Object { [string]$_.name -eq $zip.Name }) {
            Write-Host ""
            Write-Host "  Release $tag already has $($zip.Name); nothing was uploaded." -ForegroundColor Green
            Write-Host "  $($published.html_url)" -ForegroundColor Green
            exit 0
        }
        throw ("Release $tag is published without $($zip.Name). Add the zip on the website, or delete that" +
               "`n      release and run this again.`n      $($published.html_url)")
    }
    $latest = (Invoke-GitHub GET "/repos/$Repo/releases/latest" $null @(404)).Data
    if ($latest) {
        Write-Note "Latest release: $($latest.tag_name)"
        $theirs = Get-TagVersion ([string]$latest.tag_name)
        if ($theirs -and $theirs -gt [version]$stage.Version) {
            throw "The latest release is $($latest.tag_name), newer than $tag. Nothing was uploaded."
        }
    } else {
        Write-Note 'No release yet'
    }

    # Drafts are listed only to a login that can push, which the account check made sure of
    $all = @((Invoke-GitHub GET "/repos/$Repo/releases?per_page=100").Data)
    $draft = $all | Where-Object { $_.draft -and [string]$_.tag_name -eq $tag } | Select-Object -First 1
    $fields = @{ name = $title; body = $notes; target_commitish = $stage.Sha; draft = $true }
    if ($draft) {
        Write-Step 'Finishing the draft an earlier run left'
        $release = (Invoke-GitHub PATCH "/repos/$Repo/releases/$($draft.id)" $fields).Data
        $draftId = $release.id
        $assets = @($draft.assets)
        Write-Ok "Draft $($release.id) updated"
    } else {
        Write-Step 'Making the draft'
        $fields.tag_name = $tag
        $fields.prerelease = $false
        try {
            $release = (Invoke-GitHub POST "/repos/$Repo/releases" $fields).Data
        } catch {
            # GitHub cannot point a new tag at a commit it does not have
            throw "$($_.Exception.Message)`n      Is commit $($stage.Sha) pushed to $($Repo)?"
        }
        $draftId = $release.id
        $assets = @()
        Write-Ok "Draft $($release.id) made, tag $tag at $($stage.Sha)"
    }

    Write-Step "Uploading $($zip.Name)"
    $kept = $false
    foreach ($asset in $assets) {
        if ([string]$asset.name -ne $zip.Name) { continue }
        if (-not $kept -and (Test-GoodAsset $asset $zip)) {
            $kept = $true
            Write-Ok "Already on the draft ($($asset.size) bytes)"
            continue
        }
        Write-Note "Removing an incomplete or different upload ($($asset.state), $($asset.size) bytes)"
        Invoke-GitHub DELETE "/repos/$Repo/releases/assets/$($asset.id)" | Out-Null
    }
    if (-not $kept) {
        Send-Zip $release $zip
        Write-Ok "Uploaded, $($zip.Bytes.Length) bytes"
    }

    Write-Step 'Publishing'
    $publishing = $true
    $done = (Invoke-GitHub PATCH "/repos/$Repo/releases/$draftId" @{ draft = $false; make_latest = 'true' }).Data
    if ($done.draft) { throw "GitHub kept release $tag as a draft." }

    Write-Host ""
    Write-Host "  Done: $($done.html_url)" -ForegroundColor Green
} catch {
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($publishing -and $NoAnswer) {
        Write-Host "  No answer came to publishing, so the release may be public anyway. Run this again: it stops" `
            -ForegroundColor Yellow
        Write-Host "  at a published release with the zip, and finishes the draft if it is still one." `
            -ForegroundColor Yellow
    } elseif ($draftId) {
        Write-Host "  A draft release of $tag is on GitHub, which only the owner can see. Running this again" `
            -ForegroundColor Yellow
        Write-Host "  finishes it." -ForegroundColor Yellow
    } else {
        Write-Host "  Nothing was put on GitHub." -ForegroundColor Yellow
    }
    exit 1
}
