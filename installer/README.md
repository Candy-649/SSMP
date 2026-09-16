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
    BepInEx_win_x64_<version>.zip     <- third-party binary, not in this repo
    SSMP/
        SSMP.dll                      <- build output, not in this repo
        BouncyCastle.Cryptography.dll <- build output, not in this repo
```

Everything sits flat beside `install.ps1`, which locates both by pattern: any `BepInEx*.zip` next to it is accepted,
and every `*.dll` in `SSMP/` is copied into `BepInEx/plugins/SSMP`. Build the plugin with
`dotnet build SSMP/SSMP.csproj -c Release -p:LangVersion=preview` and take the DLLs from `bin/Release/`.

Verify the hash of `SSMP/SSMP.dll` inside the finished archive against the one you just built. A stale DLL in a
freshly named archive looks exactly like a fix that did not work.

## Two things to keep this way

**Console output is English on purpose.** Windows PowerShell 5.1 reads a UTF-8 file without a BOM as ANSI, so any
other language turns into mojibake on a machine that has not been set up for it. `INSTALL.txt` carries the readable
explanation instead, and is Chinese on purpose.

**`INSTALL.txt` is the only place that describes how to play.** The script used to print a short version of it too,
which went stale twice while the flow changed. If the flow changes again, change `INSTALL.txt` and nothing else.

## What it touches

The game folder, plus one backup of the save folder into `save-backups/` beside the script (the newest `$KeepBackups`
are kept; only files matching the name this script writes, in the folder this script made, are ever removed). No
registry writes, no system settings, no background services, and no network access at all when the loader archive is
bundled — downloading is only a fallback for when it is missing.
