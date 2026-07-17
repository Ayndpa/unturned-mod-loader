#Requires -Version 5.1
<#
  Install WinFsp (user-mode file system). Requires administrator.
  Uses winfsp-*.msi in this folder or cache\, else downloads from GitHub.

  Version selection:
    - Prefer the MSI that matches the managed winfsp.net package (2.2.26194).
    - Scans BOTH stable and pre-release GitHub releases (latest alone skips betas).
    - Falls back to a pinned 2.2.26194 URL if the API is unreachable.

  -Mirror selects the download source (the C# app has already speed-tested it).
  -KeepOpen keeps the elevated PowerShell window open after a successful install
  (errors always wait for Enter so the user can read the message).
#>
[CmdletBinding()]
param(
    [switch]$SkipDownload,
    [switch]$KeepOpen,
    [ValidateSet('Direct','GhProxyCom','GhProxyOrg','V4GhProxyOrg','V6GhProxyOrg','CdnGhProxyOrg')]
    [string]$Mirror = 'Direct'
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot
$CheckScript = Join-Path $ScriptDir 'Check-WinFsp.ps1'
$CacheDir = Join-Path $ScriptDir 'cache'

# Keep in sync with winfsp.net NuGet package / WinFspMirrorService.RequiredNativeVersion.
$RequiredNativeVersion = '2.2.26194'
$PinnedMsiName = "winfsp-$RequiredNativeVersion.msi"
$PinnedMsiUrl  = "https://github.com/winfsp/winfsp/releases/download/v2.2B3/$PinnedMsiName"

function Test-IsAdministrator {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-InteractiveExit {
    if ([Environment]::UserInteractive) {
        Write-Host ''
        Read-Host 'Press Enter to close'
    }
}

function Exit-WithOptionalPause {
    param(
        [int]$Code,
        [switch]$ForcePause
    )
    if ($ForcePause -or $KeepOpen -or $Code -ne 0) {
        Wait-InteractiveExit
    }
    exit $Code
}

function Invoke-CheckScript {
    if (-not (Test-Path -LiteralPath $CheckScript)) {
        Write-Warning "Check script not found: $CheckScript"
        return 1
    }
    & $CheckScript
    return $LASTEXITCODE
}

function Get-MirrorPrefix {
    param([string]$MirrorName)
    switch ($MirrorName) {
        'GhProxyCom'    { return 'https://gh-proxy.com/' }
        'GhProxyOrg'    { return 'https://gh-proxy.org/' }
        'V4GhProxyOrg'  { return 'https://v4.gh-proxy.org/' }
        'V6GhProxyOrg'  { return 'https://v6.gh-proxy.org/' }
        'CdnGhProxyOrg' { return 'https://cdn.gh-proxy.org/' }
        default         { return '' }   # Direct
    }
}

function Get-MirroredUrl {
    param([string]$RawGithubUrl, [string]$MirrorName)
    $prefix = Get-MirrorPrefix -MirrorName $MirrorName
    return "$prefix$RawGithubUrl"
}

function Get-DownloadableMsiUrl {
    # Scan stable + pre-release releases; prefer MSI matching $RequiredNativeVersion.
    try {
        $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/winfsp/winfsp/releases?per_page=20' `
            -Headers @{
                'User-Agent' = 'UnturnedModLoader'
                'Accept'     = 'application/vnd.github+json'
            } -TimeoutSec 90

        $exact = $null
        $newest = $null
        $newestVer = $null

        foreach ($release in $releases) {
            if ($release.draft) { continue }

            foreach ($asset in @($release.assets)) {
                if ($asset.name -notmatch '^winfsp-(\d+\.\d+\.\d+)\.msi$') { continue }
                if ($asset.name -match 'debug') { continue }

                $verText = $Matches[1]
                $url = $asset.browser_download_url
                if (-not $url) { continue }

                if ($verText -eq $RequiredNativeVersion) {
                    $exact = [pscustomobject]@{ Url = $url; Name = $asset.name }
                }

                try {
                    $ver = [version]$verText
                    if ($null -eq $newestVer -or $ver -gt $newestVer) {
                        $newestVer = $ver
                        $newest = [pscustomobject]@{ Url = $url; Name = $asset.name }
                    }
                }
                catch {
                    # ignore unparsable version strings
                }
            }
        }

        if ($exact) {
            Write-Host "Resolved matched WinFsp MSI: $($exact.Name)" -ForegroundColor DarkGray
            return $exact
        }
        if ($newest) {
            Write-Host "Resolved newest WinFsp MSI: $($newest.Name)" -ForegroundColor DarkGray
            return $newest
        }
    }
    catch {
        Write-Host "GitHub API unavailable: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    Write-Host "Using pinned WinFsp MSI: $PinnedMsiName" -ForegroundColor Yellow
    return [pscustomobject]@{
        Url  = $PinnedMsiUrl
        Name = $PinnedMsiName
    }
}

function Select-PreferredLocalMsi {
    param([System.IO.FileInfo[]]$Candidates)
    if (-not $Candidates -or $Candidates.Count -eq 0) { return $null }

    $requiredName = $PinnedMsiName
    $match = $Candidates | Where-Object { $_.Name -ieq $requiredName } | Select-Object -First 1
    if ($match) { return $match }

    # Prefer highest winfsp-x.y.z.msi by version, not just newest file timestamp.
    $ranked = foreach ($f in $Candidates) {
        if ($f.Name -match '^winfsp-(\d+\.\d+\.\d+)\.msi$') {
            [pscustomobject]@{ File = $f; Ver = [version]$Matches[1] }
        }
    }
    if ($ranked) {
        return ($ranked | Sort-Object Ver -Descending | Select-Object -First 1).File
    }

    return ($Candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
}

try {
    if (-not (Test-IsAdministrator)) {
        Write-Host 'Administrator rights required. Use Mod Loader one-click install (UAC) or run this script as admin.' -ForegroundColor Red
        Exit-WithOptionalPause -Code 1 -ForcePause
    }

    $checkExit = Invoke-CheckScript
    if ($checkExit -eq 0) {
        Write-Host 'WinFsp is already installed.' -ForegroundColor Green
        Invoke-CheckScript | Out-Null
        Exit-WithOptionalPause -Code 0
    }

    $localCandidates = @(
        Get-ChildItem -Path $ScriptDir -Filter 'winfsp-*.msi' -File -ErrorAction SilentlyContinue
    )
    if (Test-Path -LiteralPath $CacheDir) {
        $localCandidates += @(
            Get-ChildItem -Path $CacheDir -Filter 'winfsp-*.msi' -File -ErrorAction SilentlyContinue
        )
    }

    $msi = Select-PreferredLocalMsi -Candidates $localCandidates

    if (-not $msi -and -not $SkipDownload) {
        Write-Host 'No local MSI; downloading WinFsp...' -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null

        $dl = Get-DownloadableMsiUrl
        if (-not $dl) {
            throw 'Could not resolve a winfsp-*.msi download URL.'
        }

        # Apply the mirror the C# app already speed-tested.
        $mirroredUrl = Get-MirroredUrl -RawGithubUrl $dl.Url -MirrorName $Mirror
        $dest = Join-Path $CacheDir $dl.Name
        if (-not (Test-Path -LiteralPath $dest)) {
            Write-Host "Download ($Mirror): $mirroredUrl"
            Invoke-WebRequest -Uri $mirroredUrl -OutFile $dest -UseBasicParsing -TimeoutSec 600
        }
        $msi = Get-Item -LiteralPath $dest
    }

    if (-not $msi) {
        Write-Host "Place winfsp-*.msi in: $ScriptDir or $CacheDir" -ForegroundColor Red
        Exit-WithOptionalPause -Code 1 -ForcePause
    }

    Write-Host "Installing: $($msi.FullName)" -ForegroundColor Cyan
    $log = Join-Path $env:TEMP ("winfsp-install-{0}.log" -f [Guid]::NewGuid().ToString('N'))

    $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @(
        '/i', $msi.FullName,
        '/qn', '/norestart',
        '/L*v', $log
    ) -Wait -PassThru -NoNewWindow

    if ($proc.ExitCode -ne 0) {
        $hint = ''
        if ($proc.ExitCode -eq 1603) { $hint = ' (often: not elevated or leftover install)' }
        if ($proc.ExitCode -eq 1618) { $hint = ' (another installer running)' }
        Write-Host "msiexec failed, exit $($proc.ExitCode)$hint" -ForegroundColor Red
        Write-Host "Log: $log" -ForegroundColor Yellow
        Exit-WithOptionalPause -Code $proc.ExitCode -ForcePause
    }

    Write-Host 'WinFsp install finished.' -ForegroundColor Green
    Invoke-CheckScript | Out-Null
    # Success: close the elevated window automatically unless -KeepOpen was passed.
    Exit-WithOptionalPause -Code 0
}
catch {
    Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
    Exit-WithOptionalPause -Code 1 -ForcePause
}
