# Zips the package that installer/pack-npm.ps1 staged, for a mod page such as Nexus Mods, where a player downloads
# one archive by hand.
#
# It packs the staged files, so the zip and the npm package of one version are the same build: run pack-npm.ps1
# first. It builds nothing. It only checks that the stage is the build of the current commit and version.
#
# The zip is laid out as the game folder, the way the other mods of the game that carry BepInEx are: unpacked into
# the folder of the game, the loader and the plugin land where they go. It holds no script and no archive. Nexus Mods
# quarantined every version that held either (2026-10-17): first ours with the loader's release zip inside, which its
# help page "Why has my mod been quarantined?" says it rejects, then, with the loader unpacked, still the version with
# INSTALL.bat and install.ps1. Its notice names executables and similar file types, VirusTotal flags and nested
# archives; the one flag on VirusTotal was the loader's winhttp.dll (1 of 67 engines), which another co-op mod of the
# game carries without being quarantined. So the installer, the updater (UPDATE.bat, update.ps1: the File Submission
# Guidelines forbid an "auto update" download anyway), package.json and the npm README stay out, and INSTALL.txt
# tells a player of the mod page to back up the saves and unpack the zip into the game folder.
#
# Console output is English for the same reason install.ps1's is: Windows PowerShell 5.1 reads a UTF-8 file without
# a BOM as ANSI and turns anything else into mojibake.

[CmdletBinding()]
param(
    # The package that pack-npm.ps1 staged.
    [string] $StageDir = (Join-Path $PSScriptRoot '..\dist-npm'),

    # Where the zip is written. Beside the repository and gitignored, like the stage.
    [string] $OutDir = (Join-Path $PSScriptRoot '..\dist-nexus')
)

$ErrorActionPreference = 'Stop'

$PackageName = 'silksong-coop-mod'

# The folder of the stage that holds the loader, BepInEx's release unpacked (see pack-npm.ps1)
$LoaderFolder = 'BepInEx_win_x64_5.4.23.4'

# Where the plugin goes in the game folder, which is where the zip puts it
$PluginPath = 'BepInEx/plugins/SSMP'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Csproj = Join-Path $RepoRoot 'SSMP\SSMP.csproj'

function Write-Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "    $text" -ForegroundColor Green }

try {
    # --- The stage must be the build of this commit and version -------------------------------------------------
    # pack-npm.ps1 refuses a dirty tree and stamps HEAD into the DLL; a stage left from an older commit would put a
    # different build on the mod page than on npm under the same version.
    Write-Step 'Checking the stage'
    $dirty = & git -C $RepoRoot status --porcelain
    if ($dirty) {
        throw ("The working tree has uncommitted changes, so no stage can describe it. Commit them and run" +
               "`n      pack-npm.ps1 again:`n      " + ($dirty -join "`n      "))
    }
    $head = (& git -C $RepoRoot rev-parse HEAD).Trim()

    $csprojText = Get-Content $Csproj -Raw
    if ($csprojText -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)\.0</Version>') {
        throw "Could not read a <Version> of the form x.y.z.0 from $Csproj"
    }
    $semver = $matches[1]

    $dll = Join-Path $StageDir 'SSMP\SSMP.dll'
    if (-not (Test-Path $dll)) { throw "Nothing is staged at $StageDir. Run pack-npm.ps1 first." }
    $product = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
    if ($product -notmatch '^([0-9]+\.[0-9]+\.[0-9]+)\.[0-9]+\+([0-9a-f]{40})$' -or
        $matches[1] -ne $semver -or $matches[2] -ne $head) {
        throw "The staged DLL says '$product', not $semver from $head. Run pack-npm.ps1 again."
    }
    Write-Ok "Stage: $PackageName $semver from $head"

    # --- Lay a copy of the stage out as the game folder ----------------------------------------------------------
    Write-Step 'Zipping'
    $name = "$PackageName-$semver"
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("pack-nexus-" + [guid]::NewGuid().ToString('N'))
    $plugin = Join-Path $root $PluginPath
    New-Item -ItemType Directory -Path $root | Out-Null
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $zip = Join-Path (Resolve-Path $OutDir).Path "$name.zip"
    try {
        # The loader first, into a folder of its own; the plugin's folder goes into the BepInEx folder it brings
        Get-ChildItem -LiteralPath (Join-Path $StageDir $LoaderFolder) -Force |
            Copy-Item -Destination $root -Recurse
        New-Item -ItemType Directory -Path $plugin -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $StageDir 'SSMP') -Filter '*.dll' |
            Copy-Item -Destination $plugin
        # The licences travel with the binaries they cover
        Copy-Item -LiteralPath (Join-Path $StageDir 'LICENSE') -Destination $plugin
        Copy-Item -LiteralPath (Join-Path $StageDir 'LICENSE-BouncyCastle.md') -Destination $plugin
        Copy-Item -LiteralPath (Join-Path $StageDir 'INSTALL.txt') -Destination $root

        if (Test-Path $zip) { Remove-Item $zip }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $root, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false
        )
    } finally {
        Remove-Item $root -Recurse -Force
    }

    # --- Verify the zip itself, not the folder it was made from ----------------------------------------------------
    Write-Step 'Verifying the zip'
    Add-Type -AssemblyName System.IO.Compression
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @('INSTALL.txt', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version',
                                'BepInEx/core/BepInEx.Preloader.dll', "$PluginPath/SSMP.dll",
                                "$PluginPath/BouncyCastle.Cryptography.dll", "$PluginPath/LICENSE",
                                "$PluginPath/LICENSE-BouncyCastle.md")) {
            if ($entries -notcontains $required) { throw "Missing from the zip: $required" }
        }
        $refused = @($entries | Where-Object {
            $_ -match '\.(zip|7z|rar|tar|gz|tgz|bz2|xz|zst|cab|jar|nupkg|bat|cmd|ps1|psm1|vbs|js|exe|msi|scr|com)$'
        })
        if ($refused) { throw "Archives or scripts in the zip, which Nexus Mods quarantines: $($refused -join ', ')" }
    } finally {
        $archive.Dispose()
    }
    $sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
    Write-Ok "$($entries.Count) entries, $sizeMb MB"
    Write-Ok "SHA256: $((Get-FileHash $zip -Algorithm SHA256).Hash)"

    Write-Host ""
    Write-Host "  Zipped $name for a mod page:" -ForegroundColor Green
    Write-Host "      $zip" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Nothing has been uploaded. After npm publish, upload-nexus.ps1 puts it on the mod page." `
        -ForegroundColor Yellow
} catch {
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
