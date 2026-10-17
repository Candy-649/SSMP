# Installs the BepInEx mod loader and the SSMP co-op plugin into Hollow Knight: Silksong.
#
# The loader ships inside this archive, so the install needs no network at all. It comes as a folder, BepInEx's
# official release unpacked, and not as the release's own zip: Nexus Mods quarantines an upload that holds another
# archive, which kept every version of the mod page's file from being downloaded (2026-10-17).
#
# Console output is English on purpose: Windows PowerShell 5.1 reads a UTF-8 file without a BOM as ANSI, which turns
# any other language into mojibake on a machine that has not been set up for it. The readable explanation lives in
# INSTALL.txt next to this script.
#
# Everything this script writes goes into the game folder, plus one backup of the save folder. It changes no system
# setting and needs no administrator rights unless the game itself sits in a protected folder.

$ErrorActionPreference = 'Stop'

$GameExe = 'Hollow Knight Silksong.exe'
$GameDirName = 'Hollow Knight Silksong'

# How many save backups to keep. Older ones are removed after a new one is made, so reinstalling a few times does not
# leave a pile behind. Only backups this script made, in the folder this script made, are ever touched.
$KeepBackups = 3

function Write-Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn2($text) { Write-Host "    $text" -ForegroundColor Yellow }

function Find-Game {
    $candidates = New-Object System.Collections.Generic.List[string]

    # Steam records where it is installed, and where its other libraries are
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
        # No Steam in the registry is not fatal; the guesses below and the prompt still apply
    }

    # Common places people put a second Steam library
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

function Backup-Saves {
    $lowLow = Join-Path $env:USERPROFILE 'AppData\LocalLow'
    if (-not (Test-Path $lowLow)) { Write-Warn2 'No LocalLow folder found, skipping the save backup.'; return }

    # Found rather than assumed, so that a different folder name does not silently skip the backup
    $saveDirs = Get-ChildItem $lowLow -Directory -Recurse -Depth 1 -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*Silksong*' }

    if (-not $saveDirs) { Write-Warn2 'No Silksong save folder found, skipping the save backup.'; return }

    # Kept in a folder beside this script rather than on the desktop. These are only ever wanted if an install goes
    # wrong, and piling them onto someone's desktop is a poor price for that. The trade is that deleting the unpacked
    # installer takes them with it, which is why the script says where they are.
    $backupDir = Join-Path $PSScriptRoot 'save-backups'

    foreach ($dir in $saveDirs) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        # The folder name is part of it: two save folders backed up within the same second would otherwise write the
        # same file name, and the -Force below would quietly overwrite the first backup with the second
        $target = Join-Path $backupDir "silksong-saves-backup-$($dir.Name)-$stamp.zip"
        try {
            New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
            Compress-Archive -Path $dir.FullName -DestinationPath $target -Force
            Write-Ok "Saves backed up to: $target"

            # Deliberately narrow: only this folder, only names this script writes, and only after the new backup
            # exists, so a failed backup never costs you the older ones
            Get-ChildItem -Path $backupDir -Filter 'silksong-saves-backup-*.zip' -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                Select-Object -Skip $KeepBackups |
                ForEach-Object {
                    Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
                    Write-Ok "Removed an older backup: $($_.Name)"
                }
        } catch {
            Write-Warn2 "Could not back up $($dir.FullName): $($_.Exception.Message)"
        }
    }
}

function Get-Loader($work) {
    # The folder that came with this installer, BepInEx's official release unpacked (see the top of this file)
    foreach ($folder in (Get-ChildItem $PSScriptRoot -Directory -Filter 'BepInEx*' -ErrorAction SilentlyContinue)) {
        $payload = Find-PayloadRoot $folder.FullName
        if ($payload) {
            Write-Ok "Using the copy that came with this installer: $($folder.Name)"
            return $payload
        }
    }

    # Any BepInEx archive placed next to this script is accepted, so a manually downloaded one works too
    $archive = Get-ChildItem $PSScriptRoot -Filter 'BepInEx*.zip' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($archive) {
        Write-Ok "Using the archive next to this installer: $($archive.Name)"
        $extracted = Join-Path $work 'extracted'
        Expand-Archive -Path $archive.FullName -DestinationPath $extracted -Force
        $payload = Find-PayloadRoot $extracted
        if (-not $payload) {
            throw 'Could not find the loader inside the archive (no folder with both BepInEx and winhttp.dll).'
        }
        return $payload
    }

    throw ('The mod loader is not next to this script. Unzip the whole archive first, then run the installer' +
           "`n      from inside it.")
}

function Find-PayloadRoot($extractedDir) {
    # The layer to copy is the one holding the loader itself, whatever the archive wraps it in. Matching on the
    # contents rather than on a folder name means a repackaged archive still installs correctly.
    $all = @(Get-Item $extractedDir) + @(Get-ChildItem $extractedDir -Recurse -Directory)
    foreach ($dir in $all) {
        $hasBepInEx = Test-Path (Join-Path $dir.FullName 'BepInEx')
        $hasProxy = (Test-Path (Join-Path $dir.FullName 'winhttp.dll')) -or
                    (Test-Path (Join-Path $dir.FullName 'version.dll'))
        if ($hasBepInEx -and $hasProxy) { return $dir.FullName }
    }

    return $null
}

try {
    Write-Host ""
    Write-Host "  Silksong co-op (SSMP) installer" -ForegroundColor White
    Write-Host "  --------------------------------"

    Write-Step 'Looking for the game'
    $game = Find-Game
    if (-not $game) {
        Write-Warn2 "Could not find $GameDirName automatically."
        Write-Host "    Paste the folder that contains $GameExe and press Enter:"
        $game = (Read-Host '    Game folder').Trim('"', ' ')
        if (-not (Test-Path (Join-Path $game $GameExe))) {
            throw "No $GameExe in: $game"
        }
    }
    Write-Ok "Game folder: $game"

    Write-Step 'Backing up saves first'
    Backup-Saves

    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("ssmp-install-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    Write-Step 'Getting the BepInEx mod loader'
    $payload = Get-Loader $work

    Write-Step 'Installing the mod loader into the game folder'
    foreach ($item in (Get-ChildItem $payload -Force)) {
        Copy-Item -Path $item.FullName -Destination $game -Recurse -Force
    }
    Write-Ok 'Mod loader copied'

    Write-Step 'Installing the co-op plugin'
    $pluginSource = Join-Path $PSScriptRoot 'SSMP'
    if (-not (Test-Path $pluginSource)) {
        throw 'No SSMP folder next to this script. Unzip the whole archive first, then run the installer from inside it.'
    }

    $pluginTarget = Join-Path $game 'BepInEx\plugins\SSMP'
    New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null
    foreach ($dll in (Get-ChildItem $pluginSource -Filter '*.dll')) {
        Copy-Item -Path $dll.FullName -Destination $pluginTarget -Force
        Write-Ok "Installed $($dll.Name)"
    }

    Write-Step 'Checking the result'
    $checks = @(
        (Join-Path $game 'winhttp.dll'),
        (Join-Path $game 'BepInEx'),
        (Join-Path $pluginTarget 'SSMP.dll')
    )
    $missing = $checks | Where-Object { -not (Test-Path $_) }
    if ($missing) {
        throw "These are missing after the install:`n      " + ($missing -join "`n      ")
    }

    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Host ""
    Write-Host "  Done. Start the game from Steam: the main menu now has 'Start Multiplayer'." -ForegroundColor Green
    # Deliberately not a summary of how to play: that changed twice already, and a second copy of it here is a
    # second copy to keep correct. INSTALL.txt is the one place that describes it.
    Write-Host "  Both players need this same installer. INSTALL.txt says how to play together." -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Nothing was started. You can run this installer again." -ForegroundColor Yellow
    Write-Host ""
    exit 1
}
