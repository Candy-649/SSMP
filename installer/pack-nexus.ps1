# Zips the package that installer/pack-npm.ps1 staged, for a mod page such as Nexus Mods, where a player downloads
# one archive by hand.
#
# It packs the staged files, so the zip and the npm package of one version are the same build: run pack-npm.ps1
# first. It builds nothing and adds nothing. It only checks that the stage is the build of the current commit and
# version, and leaves out package.json, which only npm reads, and the updater (UPDATE.bat, update.ps1). Nexus Mods'
# File Submission Guidelines prohibit files that connect to the internet to download files unless that is crucial
# for the mod to work, and say that "auto update" functionality does not qualify; a player of the mod page updates by
# downloading the new zip from it instead. The files sit in one folder named after the version inside the zip, so
# unpacking it never scatters them among other files. No archive may sit inside it: Nexus Mods quarantines such an
# upload until a moderator has looked at it, which is why the stage carries the loader unpacked (see pack-npm.ps1).
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

# What the stage has that the zip leaves out (see the top of this file)
$LeftOut = @('package.json', 'UPDATE.bat', 'update.ps1')

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

    # --- Zip a copy of the stage under one folder -------------------------------------------------------------------
    Write-Step 'Zipping'
    $name = "$PackageName-$semver"
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("pack-nexus-" + [guid]::NewGuid().ToString('N'))
    $root = Join-Path $temp $name
    New-Item -ItemType Directory -Path $root | Out-Null
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $zip = Join-Path (Resolve-Path $OutDir).Path "$name.zip"
    try {
        Get-ChildItem $StageDir -Force | Where-Object { $LeftOut -notcontains $_.Name } |
            Copy-Item -Destination $root -Recurse
        if (Test-Path $zip) { Remove-Item $zip }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $root, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true
        )
    } finally {
        Remove-Item $temp -Recurse -Force
    }

    # --- Verify the zip itself, not the folder it was made from ----------------------------------------------------
    Write-Step 'Verifying the zip'
    Add-Type -AssemblyName System.IO.Compression
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @('INSTALL.bat', 'INSTALL.txt', 'install.ps1',
                                'LICENSE', 'LICENSE-BouncyCastle.md', 'README.md',
                                'BepInEx_win_x64_5.4.23.4/winhttp.dll',
                                'BepInEx_win_x64_5.4.23.4/BepInEx/core/BepInEx.Preloader.dll',
                                'SSMP/SSMP.dll', 'SSMP/BouncyCastle.Cryptography.dll')) {
            if ($entries -notcontains "$name/$required") { throw "Missing from the zip: $name/$required" }
        }
        $nested = @($entries | Where-Object { $_ -match '\.(zip|7z|rar|tar|gz|tgz)$' })
        if ($nested) { throw "Archives inside the zip, which Nexus Mods quarantines: $($nested -join ', ')" }
        foreach ($left in $LeftOut) {
            if ($entries -contains "$name/$left") { throw "$left ended up in the zip." }
        }
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
