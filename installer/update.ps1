# Updates an already-installed SSMP co-op plugin in place, by asking npm for the newest published build.
#
# Why this is separate from install.ps1: that script's whole point is that a normal install needs no network
# at all, and it says so at the top. Adding a download to it would quietly take that property away. This one
# is the opposite - it exists to go to the network - so it lives on its own and only ever touches the plugin
# DLLs of an install that already works.
#
# Why npm: the two players are on opposite sides of the Chinese firewall. npm's mainland mirror
# (registry.npmmirror.com) serves any published tarball anonymously, with no account and no filing on either
# side, and it rewrites dist.tarball to its own host - so whichever registry answers also tells us where to
# download from, and no URL is ever built by hand here.
#
# Console output is English for the same reason install.ps1's is: Windows PowerShell 5.1 reads a UTF-8 file
# without a BOM as ANSI and turns anything else into mojibake.

[CmdletBinding()]
param(
    # Skip the search and use this game folder. Handy when the game lives somewhere unusual.
    [string] $GameDir,

    # Report what would happen and change nothing.
    [switch] $WhatIfOnly
)

$ErrorActionPreference = 'Stop'

$GameExe = 'Hollow Knight Silksong.exe'
$GameDirName = 'Hollow Knight Silksong'
$PackageName = 'silksong-coop-mod'

# The mainland mirror first because that is the side this exists for; npm itself second, as the source of
# truth for anyone outside. Both were checked to return the same dist.shasum for the same version.
$Registries = @(
    'https://registry.npmmirror.com',
    'https://registry.npmjs.org'
)

function Write-Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn2($text) { Write-Host "    $text" -ForegroundColor Yellow }

# Deliberately a second copy of install.ps1's Find-Game rather than a shared file: install.ps1 already works
# and is what the other player depends on, so it is not worth editing to serve an optional extra. If the two
# ever drift the symptom is "the updater cannot find the game", which is loud and harmless.
function Find-Game {
    $candidates = New-Object System.Collections.Generic.List[string]

    try {
        $steamPath = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
        if ($steamPath) {
            $steamPath = $steamPath -replace '/', '\'
            $candidates.Add((Join-Path $steamPath "steamapps\common\$GameDirName"))

            $vdf = Join-Path $steamPath 'steamapps\libraryfolders.vdf'
            if (Test-Path $vdf) {
                foreach ($line in (Get-Content $vdf)) {
                    if ($line -match '"path"\s+"(.+?)"') {
                        $libPath = $matches[1] -replace '\\\\', '\'
                        $candidates.Add((Join-Path $libPath "steamapps\common\$GameDirName"))
                    }
                }
            }
        }
    } catch {
        # No Steam in the registry is not fatal; the guesses below still apply
    }

    foreach ($drive in (Get-PSDrive -PSProvider FileSystem).Name) {
        $candidates.Add("${drive}:\STEAM\steamapps\common\$GameDirName")
        $candidates.Add("${drive}:\SteamLibrary\steamapps\common\$GameDirName")
        $candidates.Add("${drive}:\Steam\steamapps\common\$GameDirName")
    }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-Path (Join-Path $candidate $GameExe)) { return $candidate }
    }

    return $null
}

# "0.3.1.0+<commit>" and "0.4.0.0" both reduce to the three parts npm publishes under.
function Get-ComparableVersion($raw) {
    if ($raw -match '^([0-9]+\.[0-9]+\.[0-9]+)') { return [version] $matches[1] }
    return $null
}

try {
    Write-Host ""
    Write-Host "  Silksong co-op (SSMP) updater" -ForegroundColor White
    Write-Host "  -----------------------------"

    # --- Where it is installed ---------------------------------------------------------------------------
    Write-Step 'Looking for the install'
    $game = if ($GameDir) { $GameDir.Trim('"', ' ') } else { Find-Game }
    if (-not $game -or -not (Test-Path (Join-Path $game $GameExe))) {
        throw ("Could not find $GameDirName. Pass the folder that contains ${GameExe}:`n" +
               "      .\update.ps1 -GameDir ""D:\path\to\$GameDirName""")
    }

    $pluginDir = Join-Path $game 'BepInEx\plugins\SSMP'
    $installedDll = Join-Path $pluginDir 'SSMP.dll'
    if (-not (Test-Path $installedDll)) {
        throw ("The co-op plugin is not installed here: $installedDll`n" +
               "      This updates an existing install. Run INSTALL.bat first.")
    }

    $installedRaw = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installedDll).ProductVersion
    $installed = Get-ComparableVersion $installedRaw
    if (-not $installed) { throw "Could not read a version from the installed DLL ('$installedRaw')." }
    Write-Ok "Game:      $game"
    Write-Ok "Installed: $installed"

    # --- What is published -------------------------------------------------------------------------------
    Write-Step 'Asking what the newest build is'
    $meta = $null
    $usedRegistry = $null
    foreach ($registry in $Registries) {
        $url = "$registry/$PackageName/latest"
        try {
            $meta = Invoke-RestMethod -Uri $url -UseBasicParsing -TimeoutSec 30
            $usedRegistry = $registry
            break
        } catch {
            Write-Warn2 "$registry did not answer ($($_.Exception.Message))"
        }
    }
    if (-not $meta) {
        throw ("No registry answered, so there is nothing to compare against. Your network may be blocking`n" +
               "      both of them. Nothing was changed.")
    }
    Write-Ok "Asked: $usedRegistry"

    $latest = Get-ComparableVersion $meta.version
    if (-not $latest) { throw "The registry returned a version this cannot read ('$($meta.version)')." }
    # Taken from the answer rather than built, so a mirror that serves from its own host just works.
    $tarballUrl = $meta.dist.tarball
    $expectedSha1 = $meta.dist.shasum
    if (-not $tarballUrl -or -not $expectedSha1) { throw 'The registry answer had no tarball or no checksum.' }
    Write-Ok "Newest:    $latest"

    if ($installed -ge $latest) {
        Write-Host ""
        Write-Host "  Already up to date. Nothing to do." -ForegroundColor Green
        Write-Host ""
        return
    }

    if ($WhatIfOnly) {
        Write-Host ""
        Write-Host "  Would update $installed -> $latest from $tarballUrl" -ForegroundColor Yellow
        Write-Host "  Nothing was changed (-WhatIfOnly)." -ForegroundColor Yellow
        Write-Host ""
        return
    }

    # --- The game must not be running --------------------------------------------------------------------
    # A loaded DLL cannot be overwritten on Windows. Catching it here gives a sentence someone can act on,
    # instead of an access-denied part-way through replacing the plugin.
    Write-Step 'Checking the game is closed'
    $running = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($game, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        throw ("The game is still running ($(($running | Select-Object -First 1).ProcessName)). Close it and" +
               "`n      run this again; Windows will not let a loaded plugin be replaced.")
    }
    Write-Ok 'Closed'

    # --- Download ------------------------------------------------------------------------------------------
    Write-Step 'Downloading'
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("ssmp-update-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $tgz = Join-Path $work 'package.tgz'

    $oldProgress = $ProgressPreference
    # Not cosmetic: Invoke-WebRequest's progress bar makes a download several times slower.
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $tarballUrl -OutFile $tgz -UseBasicParsing -TimeoutSec 300
    } catch {
        throw "Could not download the update: $($_.Exception.Message)"
    } finally {
        $ProgressPreference = $oldProgress
    }
    Write-Ok "Got $([math]::Round((Get-Item $tgz).Length / 1MB, 2)) MB"

    # --- Verify before anything is touched -----------------------------------------------------------------
    # The realistic failure of a public mirror is not a forged file, it is a truncated one or an error page
    # served with a 200. SHA1 is what the registry publishes, and it catches exactly that.
    Write-Step 'Checking what was downloaded'
    $actualSha1 = (Get-FileHash $tgz -Algorithm SHA1).Hash.ToLower()
    if ($actualSha1 -ne $expectedSha1.ToLower()) {
        throw ("The download does not match the checksum the registry published.`n" +
               "      expected $expectedSha1`n      got      $actualSha1`n" +
               "      Nothing was changed. Try again, or use a different network.")
    }
    Write-Ok 'Checksum matches'

    # --- Unpack --------------------------------------------------------------------------------------------
    # npm tarballs always wrap their contents in a folder called "package".
    $extracted = Join-Path $work 'extracted'
    New-Item -ItemType Directory -Path $extracted -Force | Out-Null
    & tar -xzf $tgz -C $extracted
    if ($LASTEXITCODE -ne 0) { throw 'Could not unpack the download (tar failed).' }

    $newPluginDir = Join-Path $extracted 'package\SSMP'
    $newDll = Join-Path $newPluginDir 'SSMP.dll'
    if (-not (Test-Path $newDll)) { throw "The download did not contain SSMP.dll where expected ($newDll)." }

    $newRaw = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($newDll).ProductVersion
    $new = Get-ComparableVersion $newRaw
    if ($new -ne $latest) {
        throw ("The DLL inside the package says $new but the registry said $latest. Nothing was changed.")
    }

    # --- Replace, keeping the old one ----------------------------------------------------------------------
    Write-Step 'Replacing the plugin'
    foreach ($dll in (Get-ChildItem $newPluginDir -Filter '*.dll')) {
        $target = Join-Path $pluginDir $dll.Name
        if (Test-Path $target) {
            # Kept beside the plugin so going back is a rename, not another download. One per version, so
            # repeated updates do not pile up copies of the same build.
            $backup = "$target.$installed.bak"
            Copy-Item -LiteralPath $target -Destination $backup -Force
        }
        Copy-Item -LiteralPath $dll.FullName -Destination $target -Force
        Write-Ok "Updated $($dll.Name)"
    }

    # --- Verify the install, not the download ---------------------------------------------------------------
    Write-Step 'Checking the result'
    $nowRaw = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installedDll).ProductVersion
    $now = Get-ComparableVersion $nowRaw
    if ($now -ne $latest) { throw "After copying, the installed DLL still says $now. Expected $latest." }

    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Host ""
    Write-Host "  Updated $installed -> $latest." -ForegroundColor Green
    Write-Host "  The previous plugin is kept beside it as *.$installed.bak" -ForegroundColor Green
    Write-Host "  Both players need the same version to play together." -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Your install was not changed." -ForegroundColor Yellow
    Write-Host ""
    exit 1
}
