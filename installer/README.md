# One-click installer

The installer that is shipped to players, so that someone who does not build the mod can get it running by
double-clicking one file. Only the sources live here.

| File | What it is |
| --- | --- |
| `INSTALL.bat` | What the player double-clicks. Runs `install.ps1` with `-ExecutionPolicy Bypass` for that one run, preferring `pwsh` over `powershell`. |
| `install.ps1` | Finds the game, backs up the saves, installs the BepInEx loader and copies the plugin in, then checks the result. |
| `INSTALL.txt` | What the player reads. Deliberately in Chinese; everything else here is English. |

## Building a release archive

The archive a player receives is **not** these three files on their own — `install.ps1` refuses to run without the
two things below, and neither belongs in this repository:

```
<archive root>/
    INSTALL.bat
    INSTALL.txt
    install.ps1
    BepInEx_win_x64_<version>/        <- third-party binaries, not in this repo
    SSMP/
        SSMP.dll                      <- build output, not in this repo
        BouncyCastle.Cryptography.dll <- build output, not in this repo
```

Everything sits flat beside `install.ps1`, which locates both by pattern: a `BepInEx*` folder holding the loader
(BepInEx's release, unpacked) or a `BepInEx*.zip` next to it is accepted, and every `*.dll` in `SSMP/` is copied into
`BepInEx/plugins/SSMP`. The loader travels unpacked because Nexus Mods quarantines an upload that holds another
archive; `pack-npm.ps1` unpacks it from the release zip, whose hash it checks. Build the plugin with
`dotnet build SSMP/SSMP.csproj -c Release -p:LangVersion=preview` and take the DLLs from `bin/Release/`.

Verify the hash of `SSMP/SSMP.dll` inside the finished archive against the one you just built. A stale DLL in a
freshly named archive looks exactly like a fix that did not work.

## Two things to keep this way

**Console output is English on purpose.** Windows PowerShell 5.1 reads a UTF-8 file without a BOM as ANSI, so any
other language turns into mojibake on a machine that has not been set up for it. `INSTALL.txt` carries the readable
explanation instead, and is Chinese on purpose.

**`INSTALL.txt` is the only place that describes how to play.** The script used to print a short version of it too,
which went stale twice while the flow changed. If the flow changes again, change `INSTALL.txt` and nothing else.

## Putting a release on the mod page

`pack-npm.ps1` stages a version in `dist-npm/`, and `pack-nexus.ps1` zips that stage into `dist-nexus/` for Nexus
Mods, laid out as the game folder (the loader at the top, the plugin in `BepInEx/plugins/SSMP`) and without the
installer or the updater: Nexus Mods quarantined every zip that held an archive or the scripts (see the top of
`pack-nexus.ps1`), so a player of the mod page unpacks it into the game folder. After `npm publish`,
`upload-nexus.ps1` adds the zip to the mod page as the newest version of the file already there, through the Nexus
Mods API, so nobody uploads it by hand:

```
pwsh -NoProfile -ExecutionPolicy Bypass -File installer/upload-nexus.ps1
```

| File | What it is |
| --- | --- |
| `upload-nexus.ps1` | Checks that the zip holds the same `SSMP.dll` as the npm stage, finds the page's file, uploads, adds the version (main category, previous one archived, mod version updated) and a changelog made of the titles of the `INSTALL.txt` entries marked with that version. Stops without uploading when the file already has the version or a newer one. `-DryRun` shows what it would do. |
| `nexus-file-description.txt` | The description each new version gets under it on the Files tab. |

It needs the page owner's personal API key from https://www.nexusmods.com/settings/api-keys. The script asks for it
once and keeps it encrypted for that Windows user in `%APPDATA%\silksong-coop-mod`; it never goes into the
repository. The page's first file is uploaded on the website; the API only adds versions to a file that exists.

## Putting a release on GitHub

GitHub can carry what Nexus Mods refuses, so its release gets the npm package itself: `upload-github.ps1` zips the
stage into `dist-github/` under one top folder, with the installer and the updater and without `package.json` and the
npm `README.md`, and puts it on a GitHub Release of `Candy-649/SSMP`, tagged `v<version>` at the commit stamped into
the staged `SSMP.dll`. Run it after `npm publish`:

```
pwsh -NoProfile -ExecutionPolicy Bypass -File installer/upload-github.ps1
```

| File | What it is |
| --- | --- |
| `upload-github.ps1` | Zips the stage and checks the zip (installer, updater, loader, plugin, the staged `SSMP.dll`), checks the GitHub login belongs to the repository's owner, then makes the release as a draft, uploads the zip and only then publishes it, so a public release never lacks its zip. A draft left by a failed run is finished by the next one. Stops without uploading when the release already has the zip or a newer version is released. `-DryRun` makes the zip and shows the release without going online. |
| `github-release-notes.md` | The release's description, with `{version}` and `{changes}` (the titles of the `INSTALL.txt` entries marked with that version) filled in. |

It borrows the login git already has for github.com from git's credential helper (Git Credential Manager), the way
git asks for it before a push, or takes `GITHUB_TOKEN` when that is set; the token is never printed or saved. The
owner has a second GitHub account, so the script stops before changing anything when the login belongs to another
account. None of these files is shipped to players.

## What it touches

The game folder, plus one backup of the save folder into `save-backups/` beside the script (the newest `$KeepBackups`
are kept; only files matching the name this script writes, in the folder this script made, are ever removed). No
registry writes, no system settings, no background services, and no network access at all: the loader comes with the
package, and the script stops if it is missing.
