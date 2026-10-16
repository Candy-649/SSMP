# Has the mainland npm mirror (registry.npmmirror.com) copy the version that pack-npm.ps1 staged, once npm itself
# serves it, and checks that the mirror has it. The other player's updater reaches only the mirror from the mainland.
#
# Run it right after `npm publish`. npm now takes a minute or two to make a new version available ("Your package is
# being processed"), and a sync asked for before then copies nothing: 0.4.101's sync, asked for straight after the
# publish, found the old version on npm and left the mirror on it (2026-10-16). So this script:
#   1. waits until npm serves the staged version;
#   2. asks the mirror to sync the package and waits for the sync to finish;
#   3. checks that the mirror serves the version, and asks again if it does not yet.
#
# It only reads from npm and asks the mirror to copy what npm already serves, so running it again is safe.
#
# Console output is English for the same reason install.ps1's is: Windows PowerShell 5.1 reads a UTF-8 file without
# a BOM as ANSI.

[CmdletBinding()]
param(
    # The package that pack-npm.ps1 staged (default ..\dist-npm). Its package.json names the package and version.
    [string] $StageDir,

    # How long npm may take to serve the version before the script gives up.
    [int] $WaitMinutes = 15,

    # Only say whether npm and the mirror serve the staged version, without waiting or asking the mirror for anything.
    [switch] $CheckOnly
)

$ErrorActionPreference = 'Stop'

# Set here rather than in param(): Windows PowerShell 5.1 leaves $PSScriptRoot empty in parameter defaults under -File
if (-not $StageDir) { $StageDir = Join-Path $PSScriptRoot '..\dist-npm' }

$NpmRegistry = 'https://registry.npmjs.org'
$MirrorRegistry = 'https://registry.npmmirror.com'
$MirrorSyncs = 'https://registry-direct.npmmirror.com/-/package'

# How often npm is asked whether it serves the version yet, and how long one sync may take
$PollSeconds = 15
$SyncTimeout = [TimeSpan]::FromMinutes(3)

# How many times the mirror is asked to sync before the script gives up
$SyncTries = 3

function Write-Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Note($text) { Write-Host "    $text" }

# HttpClient rather than Invoke-RestMethod: it behaves the same in Windows PowerShell 5.1 and PowerShell 7 and hands
# back error answers without throwing
Add-Type -AssemblyName System.Net.Http
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor
    [Net.SecurityProtocolType]::Tls12
$Http = New-Object System.Net.Http.HttpClient
$Http.Timeout = [TimeSpan]::FromSeconds(30)

# The status code and text of an answer, or a status code of 0 when there was no answer at all
function Invoke-Http([string] $Method, [string] $Url) {
    $request = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod $Method), $Url)
    $request.Headers.TryAddWithoutValidation('Accept', 'application/json') | Out-Null
    # A version that was just published is not in a cached answer
    $request.Headers.TryAddWithoutValidation('Cache-Control', 'no-cache') | Out-Null
    try {
        $response = $Http.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return @{ Code = [int]$response.StatusCode; Text = $text }
    } catch {
        return @{ Code = 0; Text = $_.Exception.GetBaseException().Message }
    }
}

# Whether a registry gives this version to the question that update.ps1 asks - the package's latest version, at a URL
# no cache has seen - and serves its tarball. A mirror can answer for a single version before its latest moves on.
function Test-Latest([string] $Registry, [string] $Name, [string] $Version) {
    $stamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $answer = Invoke-Http 'GET' "$Registry/$Name/latest?t=$stamp"
    if ($answer.Code -ne 200) { return $false }
    try { $meta = $answer.Text | ConvertFrom-Json } catch { return $false }
    if ($meta.version -ne $Version -or -not $meta.dist.tarball) { return $false }
    return (Invoke-Http 'HEAD' $meta.dist.tarball).Code -eq 200
}

try {
    Write-Step 'Reading the staged version'
    $packageFile = Join-Path $StageDir 'package.json'
    if (-not (Test-Path $packageFile)) { throw "No package.json in $StageDir; run pack-npm.ps1 first" }
    $package = Get-Content $packageFile -Raw | ConvertFrom-Json
    $name = $package.name
    $version = $package.version
    Write-Ok "$name@$version"

    if ($CheckOnly) {
        foreach ($registry in $NpmRegistry, $MirrorRegistry) {
            if (Test-Latest $registry $name $version) {
                Write-Ok "$registry serves $version"
            } else {
                Write-Note "$registry does not serve $version as its latest"
            }
        }
        exit 0
    }

    Write-Step "Waiting for npm to serve $version"
    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    while (-not (Test-Latest $NpmRegistry $name $version)) {
        if ((Get-Date) -gt $deadline) {
            throw "npm still does not serve $name@$version after $WaitMinutes minutes. Was it published?"
        }
        Write-Note "Not yet; asking again in $PollSeconds s"
        Start-Sleep -Seconds $PollSeconds
    }
    Write-Ok "npm serves $version"

    $synced = $false
    for ($try = 1; $try -le $SyncTries -and -not $synced; $try++) {
        Write-Step "Asking the mirror to sync (try $try of $SyncTries)"
        $answer = Invoke-Http 'PUT' "$MirrorSyncs/$name/syncs"
        $task = $null
        try { $task = $answer.Text | ConvertFrom-Json } catch { }
        if ($answer.Code -lt 200 -or $answer.Code -ge 300 -or -not $task.id) {
            Write-Note "The mirror did not take the request ($($answer.Code)): $($answer.Text)"
        } else {
            Write-Note "Sync task $($task.id)"
            $taskDeadline = (Get-Date) + $SyncTimeout
            $state = $task.state
            while ($state -ne 'success' -and $state -ne 'fail' -and (Get-Date) -lt $taskDeadline) {
                Start-Sleep -Seconds 3
                $status = Invoke-Http 'GET' "$MirrorSyncs/$name/syncs/$($task.id)"
                try { $state = ($status.Text | ConvertFrom-Json).state } catch { }
            }
            Write-Note "Sync task state: $state"
        }

        # The mirror's own answer decides, whatever the task said
        for ($look = 0; $look -lt 4 -and -not $synced; $look++) {
            if ($look -gt 0) { Start-Sleep -Seconds 5 }
            $synced = Test-Latest $MirrorRegistry $name $version
        }
        if (-not $synced -and $try -lt $SyncTries) {
            Write-Note "The mirror does not serve $version yet"
            Start-Sleep -Seconds 10
        }
    }
    if (-not $synced) {
        throw ("The mirror still does not serve $name@$version. Run this again later, or open " +
            "https://npmmirror.com/sync/$name")
    }
    Write-Ok "The mirror serves $version"

    Write-Host ""
    Write-Host "  $name@$version is on npm and on the mainland mirror." -ForegroundColor Green
    Write-Host ""
} catch {
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    exit 1
}
