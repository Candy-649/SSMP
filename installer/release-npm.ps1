# Releases the version that pack-npm.ps1 staged, with no npm login, token or 2FA prompt: pushes its tag, v<version>,
# to GitHub at the commit the staged SSMP.dll was built from, where .github/workflows/publish.yml builds that commit,
# publishes it with npm's trusted publishing, has the mainland mirror copy it and puts the zip on a GitHub Release.
# Then it waits until npm, the mirror and GitHub have the version, and says so.
#
# This is the first of the two release steps, in place of `npm publish`, sync-mirror.ps1 and upload-github.ps1 by
# hand (USER 10-25: "这个npm login还有老是要让我打开网页认证好烦啊", then "github和npm这样不能合并吗"). The second,
# upload-nexus.ps1, comes after the two players have played the version. A tag at an older commit, whose workflow
# doesn't release on GitHub yet, leaves upload-github.ps1 to the second step, as before.
#
# Publishing is public and cannot be undone after 72 hours, so this is run by the repository's owner, never by a build.
# Running it again is safe: a version that npm has already is not pushed again, and only waited for.
#
# Console output is English for the same reason install.ps1's is: Windows PowerShell 5.1 reads a UTF-8 file without
# a BOM as ANSI and turns anything else into mojibake.

[CmdletBinding()]
param(
    # The package that pack-npm.ps1 staged (default ..\dist-npm).
    [string] $StageDir,

    # The remote of the repository whose workflow publishes.
    [string] $Remote = 'origin',

    # How long npm may take to serve the version, the build on GitHub included, before this gives up.
    [int] $WaitMinutes = 20
)

$ErrorActionPreference = 'Stop'

# Set here rather than in param(): Windows PowerShell 5.1 leaves $PSScriptRoot empty in parameter defaults under -File
if (-not $StageDir) { $StageDir = Join-Path $PSScriptRoot '..\dist-npm' }

$PackageName = 'silksong-coop-mod'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Workflow = 'https://github.com/Candy-649/SSMP/actions/workflows/publish.yml'
$GitHubApi = 'https://api.github.com/repos/Candy-649/SSMP'
$Releases = 'https://github.com/Candy-649/SSMP/releases'
$NexusScript = Join-Path $PSScriptRoot 'upload-nexus.ps1'
$UploadScript = Join-Path $PSScriptRoot 'upload-github.ps1'
$Npm = 'https://registry.npmjs.org'
$Mirror = 'https://registry.npmmirror.com'

function Write-Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Note($text) { Write-Host "    $text" }

# The version that a registry serves as the newest, or $null when it can't be asked
function Get-Latest([string] $registry) {
    try {
        $answer = Invoke-RestMethod "$registry/$PackageName/latest" -TimeoutSec 20 -Headers @{ 'Cache-Control' = 'no-cache' }
        return [string]$answer.version
    } catch {
        return $null
    }
}

# Whether npm has a version at all, newest or not
function Test-OnNpm([string] $version) {
    try {
        return [string](Invoke-RestMethod "$Npm/$PackageName/$version" -TimeoutSec 20).version -eq $version
    } catch {
        return $false
    }
}

# Waits until a registry serves the version as the newest, printing a dot now and then
function Wait-Latest([string] $registry, [string] $version, [int] $minutes) {
    $until = (Get-Date).AddMinutes($minutes)
    while ((Get-Date) -lt $until) {
        if ((Get-Latest $registry) -eq $version) {
            Write-Host ""
            return $true
        }

        Write-Host -NoNewline '.'
        Start-Sleep -Seconds 15
    }

    Write-Host ""
    return $false
}

# Whether the workflow at a commit puts the version on GitHub Releases too, which older ones left to upload-github.ps1
# by hand. Read from the commit itself, since a tag runs the workflow of the commit it points at
function Test-ReleasesOnGitHub([string] $sha) {
    try {
        $text = (& git -C $RepoRoot show "$($sha):.github/workflows/publish.yml" 2>$null) -join "`n"
        return ($LASTEXITCODE -eq 0 -and $text -match '(?m)^\s*run:\s*\./installer/upload-github\.ps1\s*$')
    } catch {
        # Windows PowerShell 5.1 turns git's error output into an exception under $ErrorActionPreference = 'Stop'
        return $false
    }
}

# Whether GitHub shows the release of a tag with its zip, as anyone sees it, or $null when it gave no answer
function Test-OnGitHub([string] $tag, [string] $zipName) {
    try {
        $release = Invoke-RestMethod "$GitHubApi/releases/tags/$tag" -TimeoutSec 20 -Headers @{
            Accept = 'application/vnd.github+json'
        }
    } catch {
        $code = 0
        try { $code = [int]$_.Exception.Response.StatusCode } catch { }
        if ($code -eq 404) { return $false }
        # GitHub answers 60 questions an hour from one address without a login, then 403 or 429 (review)
        if ($code -eq 403 -or $code -eq 429) { $script:GitHubLimited = $true }
        return $null
    }
    return [bool](@($release.assets) | Where-Object { [string]$_.name -eq $zipName -and [string]$_.state -eq 'uploaded' })
}

# Waits until GitHub shows the release with its zip, printing a dot now and then: $true once it does, $false when it
# doesn't in time, $null when GitHub stops answering without a login. Asked every 20 s, for those 60 an hour
function Wait-GitHub([string] $tag, [string] $zipName, [int] $minutes) {
    $until = (Get-Date).AddMinutes($minutes)
    $script:GitHubLimited = $false
    while ($true) {
        if (Test-OnGitHub $tag $zipName) {
            Write-Host ""
            return $true
        }
        if ($script:GitHubLimited) {
            Write-Host ""
            return $null
        }
        if ((Get-Date) -ge $until) { break }

        Write-Host -NoNewline '.'
        Start-Sleep -Seconds 20
    }

    Write-Host ""
    return $false
}

# Dot-sourcing loads the functions only, for testing them one at a time
if ($MyInvocation.InvocationName -eq '.') { return }

try {
    Write-Step 'Reading the stage'
    $packageFile = Join-Path $StageDir 'package.json'
    if (-not (Test-Path $packageFile)) { throw "Nothing is staged at $StageDir. Run pack-npm.ps1 first." }
    $version = [string](Get-Content $packageFile -Raw | ConvertFrom-Json).version
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "The staged version '$version' is not x.y.z." }

    # pack-npm.ps1 stamps "<version>.<n>+<commit>" into the DLL, and the workflow builds that commit again
    $dll = Join-Path $StageDir 'SSMP\SSMP.dll'
    if (-not (Test-Path $dll)) { throw "The stage has no SSMP\SSMP.dll. Run pack-npm.ps1 again." }
    $product = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
    if ($product -notmatch ('^' + [regex]::Escape($version) + '\.[0-9]+\+([0-9a-f]{40})$')) {
        throw "The staged SSMP.dll says '$product', not $version with a commit. Run pack-npm.ps1 again."
    }
    $sha = $matches[1]
    $tag = "v$version"
    Write-Ok "$PackageName $version, built from $($sha.Substring(0, 7))"

    # Whether a run of the workflow was set going for the version, by this run or an earlier one. One that npm had
    # before, published by hand, had none, unless its tag is on GitHub (review)
    $workflowRan = $true
    if (Test-OnNpm $version) {
        Write-Ok "npm has $version already, so nothing is pushed"
        try {
            $remoteTag = (& git -C $RepoRoot ls-remote --tags $Remote "refs/tags/$tag" 2>$null)
            if ($LASTEXITCODE -eq 0) { $workflowRan = [bool]$remoteTag }
        } catch {
            # Windows PowerShell 5.1 turns git's error output into an exception; then it is waited for as usual
        }
    } else {
        Write-Step "Tagging $tag"
        $local = (& git -C $RepoRoot rev-parse -q --verify "refs/tags/$tag^{commit}" 2>$null)
        if ($local) {
            $local = $local.Trim()
            if ($local -ne $sha) { throw "Tag $tag is here already, at $local, not at $sha. Nothing was pushed." }
            Write-Ok "Tag $tag is here already, at $($sha.Substring(0, 7))"
        } else {
            & git -C $RepoRoot tag $tag $sha
            if ($LASTEXITCODE -ne 0) { throw "Could not make tag $tag. Nothing was pushed." }
            Write-Ok "Made tag $tag at $($sha.Substring(0, 7))"
        }

        $remoteTag = (& git -C $RepoRoot ls-remote --tags $Remote "refs/tags/$tag" 2>$null)
        if ($LASTEXITCODE -ne 0) { throw "Could not ask $Remote for its tags. Nothing was pushed." }
        if ($remoteTag) {
            $there = ($remoteTag -split '\s+')[0]
            if ($there -ne $sha) {
                throw "Tag $tag is on $Remote already, at $there, not at $sha. Nothing was pushed."
            }
            Write-Ok "Tag $tag is on $Remote already"
        } else {
            Write-Step "Pushing $tag to $Remote, which has GitHub publish it"
            & git -C $RepoRoot push $Remote "refs/tags/$tag"
            if ($LASTEXITCODE -ne 0) { throw "Pushing tag $tag failed. Nothing was published." }
            Write-Ok "Pushed"
        }
    }

    Write-Step "Waiting for npm to serve $version (the build on GitHub takes a few minutes)"
    Write-Note "To watch it: $Workflow"
    if (-not (Wait-Latest $Npm $version $WaitMinutes)) {
        throw "npm did not serve $version within $WaitMinutes minutes. See $Workflow for what went wrong."
    }
    Write-Ok "npm serves $version"

    Write-Step "Waiting for the mainland mirror to serve $version"
    if (-not (Wait-Latest $Mirror $version 5)) {
        $syncScript = Join-Path $PSScriptRoot 'sync-mirror.ps1'
        throw ("The mirror does not serve $version yet. Have it copy npm with:" +
               "`n      pwsh -NoProfile -ExecutionPolicy Bypass -File $syncScript")
    }
    Write-Ok "The mirror serves $version"

    $releases = Test-ReleasesOnGitHub $sha
    $onGitHub = $releases -and $workflowRan
    $seen = $false
    if ($onGitHub) {
        $zipName = "$PackageName-$version.zip"
        Write-Step "Waiting for GitHub to show release $tag with $zipName"
        $seen = Wait-GitHub $tag $zipName 10
        if ($null -eq $seen) {
            # The workflow makes the release all the same; only this check is cut short
            Write-Note "GitHub stopped answering without a login for now, so look for the release yourself."
        } elseif (-not $seen) {
            throw ("npm and the mirror serve $version, but GitHub shows no release $tag with $zipName after 10" +
                   " minutes.`n      See $Workflow for what went wrong. To put it there from this PC:" +
                   "`n      pwsh -NoProfile -ExecutionPolicy Bypass -File $UploadScript")
        } else {
            Write-Ok "GitHub shows $tag with $zipName"
        }
    }

    Write-Host ""
    if ($onGitHub) {
        $where = if ($seen) { 'npm, the mirror and GitHub' } else { 'npm and the mirror' }
        Write-Host "  $PackageName@$version is out on $where. Both players can update with UPDATE.bat." -ForegroundColor Green
        Write-Host "  $Releases/tag/$tag" -ForegroundColor Green
        Write-Host ""
        Write-Host "  Once you two have played it, put it on Nexus Mods:" -ForegroundColor Yellow
        Write-Host "      pwsh -NoProfile -ExecutionPolicy Bypass -File $NexusScript" -ForegroundColor Yellow
    } else {
        Write-Host "  $PackageName@$version is out on npm and the mirror. Both players can update with UPDATE.bat." -ForegroundColor Green
        Write-Host ""
        if ($releases) {
            Write-Host "  npm had it already and its tag isn't on GitHub, so no run of the workflow released it there." -ForegroundColor Yellow
        } else {
            Write-Host "  The workflow of $($sha.Substring(0, 7)) doesn't release on GitHub." -ForegroundColor Yellow
        }
        Write-Host "  Once you two have played it, put it on Nexus Mods and GitHub:" -ForegroundColor Yellow
        Write-Host "      pwsh -NoProfile -ExecutionPolicy Bypass -File $NexusScript && pwsh -NoProfile -ExecutionPolicy Bypass -File $UploadScript" -ForegroundColor Yellow
    }
    Write-Host ""
} catch {
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    exit 1
}
