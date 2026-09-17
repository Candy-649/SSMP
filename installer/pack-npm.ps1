# Stages the npm package that carries a release to the other player, and stops short of publishing it.
#
# Why npm at all: the two players are on opposite sides of the Chinese firewall, and npm's mainland mirror
# (registry.npmmirror.com) serves any published tarball anonymously, at a URL whose shape has not changed in
# years, with no account, no ICP filing and no real-name check on either side. That is the one property no
# Chinese cloud offers a publisher who is outside the country.
#
# This script deliberately does NOT run `npm publish`. Publishing is public and effectively permanent - npm
# refuses to unpublish after 72 hours and keeps the name reserved either way - so the last step stays a
# decision a person makes, not something a script does while you are reading its output.
#
# Console output is English for the same reason install.ps1's is: Windows PowerShell 5.1 reads a UTF-8 file
# without a BOM as ANSI and turns anything else into mojibake.

[CmdletBinding()]
param(
    # The BepInEx loader archive. It is a third-party binary and stays out of the repository on purpose (see
    # installer/README.md), so it lives in a gitignored folder rather than being committed or downloaded silently.
    [string] $BepInExZip = (Join-Path $PSScriptRoot '..\dist-assets\BepInEx_win_x64_5.4.23.4.zip'),

    # Where the staged package is assembled. Defaults beside the repository so a stale stage is easy to spot.
    [string] $OutDir = (Join-Path $PSScriptRoot '..\dist-npm'),

    # Skip the build and use whatever is already in bin/Release. Only for iterating on this script itself.
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'

$PackageName = 'silksong-coop-mod'

# Pinned so a swapped or truncated loader archive fails here rather than on the other player's machine. If the
# loader is ever upgraded on purpose, change this hash in the same commit that changes the file.
$BepInExSha256 = 'F881201B79DA03E513BF97CDF39607FFA7F9E0D31A519B1AEECA8EB60F8309E7'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Csproj = Join-Path $RepoRoot 'SSMP\SSMP.csproj'
$BuiltDir = Join-Path $RepoRoot 'bin\Release\netstandard2.1'

function Write-Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn2($text) { Write-Host "    $text" -ForegroundColor Yellow }

try {
    # --- The tree must match what gets stamped into the DLL -------------------------------------------------
    # The .NET SDK stamps the current commit into AssemblyInformationalVersion. If the tree is dirty, or the
    # build is older than HEAD, the package would carry a DLL that no commit describes - which is exactly the
    # situation where "the fix did not work" and "the fix was never in the build" look identical.
    Write-Step 'Checking the working tree'
    $dirty = & git -C $RepoRoot status --porcelain
    if ($dirty) {
        throw ("The working tree has uncommitted changes. Commit or stash them first, so the commit stamped" +
               "`n      into the DLL describes exactly what is in this package:`n      " + ($dirty -join "`n      "))
    }
    $head = (& git -C $RepoRoot rev-parse HEAD).Trim()
    Write-Ok "HEAD: $head"

    # --- Version, with the csproj as the single source of truth ---------------------------------------------
    Write-Step 'Reading the version'
    $csprojText = Get-Content $Csproj -Raw
    if ($csprojText -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)\.([0-9]+)</Version>') {
        throw "Could not read a four-part <Version> from $Csproj"
    }
    $semver = $matches[1]
    $fourth = $matches[2]
    if ($fourth -ne '0') {
        # npm versions are three-part. Silently dropping a non-zero fourth part would publish a version that
        # does not say what it contains, so refuse instead.
        throw "The fourth part of <Version> is $fourth, but npm takes three parts. Use x.y.z.0."
    }
    Write-Ok "Version: $semver"

    # --- Build ------------------------------------------------------------------------------------------------
    if (-not $NoBuild) {
        Write-Step 'Building'
        # LangVersion=preview because the code uses the C# 14 `field` keyword and the installed SDK is 9.
        # Rebuild rather than an incremental build: an incremental one reprints only warning counts, so a new
        # warning would not be visible here.
        & dotnet build $Csproj -c Release -p:LangVersion=preview -t:Rebuild -tl:off -v:minimal
        if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
        Write-Ok 'Built'
    } else {
        Write-Warn2 'Skipping the build (-NoBuild); the DLL below may be older than HEAD.'
    }

    # --- The DLL must agree with both the csproj and HEAD ------------------------------------------------------
    Write-Step 'Checking the built DLL'
    $dll = Join-Path $BuiltDir 'SSMP.dll'
    if (-not (Test-Path $dll)) { throw "No built DLL at $dll" }

    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
    $product = $info.ProductVersion      # e.g. 0.4.0.0+<40 hex chars>
    if ($product -notmatch '^([0-9]+\.[0-9]+\.[0-9]+)\.[0-9]+\+([0-9a-f]{40})$') {
        throw "The DLL's ProductVersion is '$product', which does not look like '<version>+<commit>'."
    }
    if ($matches[1] -ne $semver) {
        throw "The DLL says version $($matches[1]) but the csproj says $semver. Rebuild."
    }
    if ($matches[2] -ne $head) {
        throw ("The DLL was built from $($matches[2]) but HEAD is $head. Rebuild, so the package cannot" +
               "`n      carry a DLL from a different commit than the one it claims.")
    }
    $dllHash = (Get-FileHash $dll -Algorithm SHA256).Hash
    Write-Ok "DLL matches the csproj version and HEAD"

    # --- The loader archive ------------------------------------------------------------------------------------
    Write-Step 'Checking the BepInEx loader archive'
    if (-not (Test-Path $BepInExZip)) {
        throw ("No loader archive at: $BepInExZip`n" +
               "      It is a third-party binary kept out of the repository on purpose. Put a copy there, or pass`n" +
               "      -BepInExZip <path>. The official one is BepInExPack_Silksong on Thunderstore.")
    }
    $loaderHash = (Get-FileHash $BepInExZip -Algorithm SHA256).Hash
    if ($loaderHash -ne $BepInExSha256) {
        throw ("The loader archive does not match the pinned hash.`n" +
               "      expected $BepInExSha256`n      got      $loaderHash`n" +
               "      If the loader was upgraded on purpose, update `$BepInExSha256 in this script.")
    }
    Write-Ok 'Loader archive matches the pinned hash'

    # --- Stage --------------------------------------------------------------------------------------------------
    Write-Step 'Staging the package'
    if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    $pluginDir = Join-Path $OutDir 'SSMP'
    New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null

    # Only what the installer actually needs at runtime. SSMP.xml (1.9 MB of doc comments), SSMP.pdb and
    # SSMP.deps.json are build by-products and would triple the download for no benefit to a player.
    Copy-Item (Join-Path $PSScriptRoot 'INSTALL.bat') $OutDir
    Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $OutDir
    Copy-Item (Join-Path $PSScriptRoot 'INSTALL.txt') $OutDir
    Copy-Item (Join-Path $PSScriptRoot 'LICENSE-BouncyCastle.md') $OutDir
    Copy-Item $BepInExZip $OutDir
    Copy-Item $dll $pluginDir
    Copy-Item (Join-Path $BuiltDir 'BouncyCastle.Cryptography.dll') $pluginDir

    # The mod is LGPL-2.1 and this package is a public redistribution of it, so the licence text has to travel
    # with the binary. The offline zip handed to one friend never carried it; a public npm package must.
    Copy-Item (Join-Path $RepoRoot 'LICENSE') $OutDir

    # --- package.json, generated so the version has exactly one source ------------------------------------------
    # No e-mail address anywhere in here: every field of a published package.json is public forever.
    $pkg = [ordered]@{
        name        = $PackageName
        version     = $semver
        description = 'Offline installer payload for a two-player co-op build of the SSMP Silksong multiplayer mod. Not a Node library.'
        license     = 'LGPL-2.1-or-later'
        author      = 'Candy-649 (https://github.com/Candy-649)'
        homepage    = 'https://github.com/Candy-649/SSMP'
        repository  = [ordered]@{ type = 'git'; url = 'git+https://github.com/Candy-649/SSMP.git' }
        keywords    = @('silksong', 'bepinex', 'mod', 'installer')
        # No "main", no "bin", no lifecycle "scripts": this is a data package fetched as a tarball, never
        # required into a program and never run on install.
        files       = @('INSTALL.bat', 'INSTALL.txt', 'install.ps1', 'LICENSE', 'LICENSE-BouncyCastle.md',
                        'BepInEx_win_x64_5.4.23.4.zip', 'SSMP/', 'README.md')
    }
    $pkgJson = $pkg | ConvertTo-Json -Depth 5
    Set-Content -Path (Join-Path $OutDir 'package.json') -Value $pkgJson -Encoding UTF8

    # A player-facing README. installer/README.md is the maintainer's build note and would show build steps to
    # someone who only wants to install, so it is deliberately not the one published here.
    $readme = @"
# silksong-coop-mod $semver

The files a player needs to install a two-player co-op build of **SSMP**, packaged so they can be fetched from
a mirror that is reachable inside mainland China. This is not a Node library: nothing here is imported or run
by ``npm install``.

## Install

Download and unpack the tarball, then double-click ``INSTALL.bat``. ``INSTALL.txt`` explains the rest, in Chinese.

The BepInEx loader is included, so the install needs no network access at all.

## Credit and licence

The multiplayer mod is the work of [Extremelyd1](https://github.com/Extremelyd1/SSMP). This is a modified copy
that adds a shared two-player save; please do not take questions about it to the original author. Distributed
under the GNU LGPL 2.1 - see ``LICENSE``. Built from https://github.com/Candy-649/SSMP at ``$($head.Substring(0,7))``.
"@
    Set-Content -Path (Join-Path $OutDir 'README.md') -Value $readme -Encoding UTF8

    # --- Verify the staged copy, not the one we meant to copy ---------------------------------------------------
    Write-Step 'Verifying the staged package'
    $stagedDll = Join-Path $pluginDir 'SSMP.dll'
    $stagedHash = (Get-FileHash $stagedDll -Algorithm SHA256).Hash
    if ($stagedHash -ne $dllHash) { throw 'The staged DLL does not match the built one.' }

    foreach ($required in @('INSTALL.bat', 'INSTALL.txt', 'install.ps1', 'LICENSE', 'LICENSE-BouncyCastle.md',
                            'package.json', 'README.md', 'BepInEx_win_x64_5.4.23.4.zip',
                            'SSMP\SSMP.dll', 'SSMP\BouncyCastle.Cryptography.dll')) {
        if (-not (Test-Path (Join-Path $OutDir $required))) { throw "Missing from the stage: $required" }
    }
    $totalMb = [math]::Round(((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 2)
    Write-Ok "All expected files present, $totalMb MB"
    Write-Ok "DLL SHA256: $dllHash"

    Write-Host ""
    Write-Host "  Staged $PackageName@$semver in:" -ForegroundColor Green
    Write-Host "      $((Resolve-Path $OutDir).Path)" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Nothing has been published. To look at the exact tarball first:" -ForegroundColor Yellow
    Write-Host "      npm pack --dry-run --pack-destination . " -ForegroundColor Yellow
    Write-Host "  and when you are ready, from inside that folder:" -ForegroundColor Yellow
    Write-Host "      npm publish --access public" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Publishing is public and cannot be undone after 72 hours, and the name stays taken either way." -ForegroundColor Yellow
    Write-Host ""
} catch {
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Nothing was published." -ForegroundColor Yellow
    Write-Host ""
    exit 1
}
