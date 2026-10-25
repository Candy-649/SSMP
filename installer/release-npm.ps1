# Releases the version that pack-npm.ps1 staged on npm, with no npm login, token or 2FA prompt: pushes its tag,
# v<version>, to GitHub at the commit the staged SSMP.dll was built from, where .github/workflows/publish.yml builds
# that commit, publishes it with npm's trusted publishing and has the mainland mirror copy it. Then it waits until npm
# and the mirror serve the version, and says so.
#
# This is the first of the two release steps, in place of `npm publish` and sync-mirror.ps1 by hand (USER 10-25:
# "这个npm login还有老是要让我打开网页认证好烦啊"). The second, upload-nexus.ps1 and upload-github.ps1, comes after
# the two players have played the version; the GitHub release then takes the tag that this pushed.
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

    if (Test-OnNpm $version) {
        Write-Ok "npm has $version already, so nothing is pushed"
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

    Write-Host ""
    Write-Host "  $PackageName@$version is out on npm and the mirror. Both players can update with UPDATE.bat." -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    exit 1
}
