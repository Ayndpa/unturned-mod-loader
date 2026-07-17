#Requires -Version 5.1
<#
  Install WinFsp (user-mode file system). Requires administrator.
  Uses winfsp-*.msi in this folder or cache\, else downloads from GitHub.
  -Mirror selects the download source (the C# app has already speed-tested it).
#>
[CmdletBinding()]
param(
    [switch]$SkipDownload,
    [ValidateSet('Direct','GhProxyCom','GhProxyOrg','V4GhProxyOrg','V6GhProxyOrg','CdnGhProxyOrg')]
    [string]$Mirror = 'Direct'
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot
$CheckScript = Join-Path $ScriptDir 'Check-WinFsp.ps1'
$CacheDir = Join-Path $ScriptDir 'cache'

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
    # Returns the raw github.com release URL of the latest winfsp-*.msi.
    # Falls back to a pinned release if the GitHub API is unavailable.
    try {
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/winfsp/winfsp/releases/latest' `
            -Headers @{ 'User-Agent' = 'UnturnedModLoader' } -TimeoutSec 90
        $asset = $release.assets | Where-Object {
            $_.name -match '^winfsp-[\d.]+\.msi$' -and $_.name -notmatch 'debug'
        } | Select-Object -First 1
        if ($asset) {
            return [pscustomobject]@{ Url = $asset.browser_download_url; Name = $asset.name }
        }
    }
    catch {
        Write-Host "GitHub API unavailable: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    return [pscustomobject]@{
        Url  = 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi'
        Name = 'winfsp-2.1.25156.msi'
    }
}

try {
    if (-not (Test-IsAdministrator)) {
        Write-Host 'Administrator rights required. Use Mod Loader one-click install (UAC) or run this script as admin.' -ForegroundColor Red
        Wait-InteractiveExit
        exit 1
    }

    $checkExit = Invoke-CheckScript
    if ($checkExit -eq 0) {
        Write-Host 'WinFsp is already installed.' -ForegroundColor Green
        Invoke-CheckScript | Out-Null
        Wait-InteractiveExit
        exit 0
    }

    $msi = Get-ChildItem -Path $ScriptDir -Filter 'winfsp-*.msi' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $msi) {
        $msi = Get-ChildItem -Path $CacheDir -Filter 'winfsp-*.msi' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
    }

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
        Wait-InteractiveExit
        exit 1
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
        Wait-InteractiveExit
        exit $proc.ExitCode
    }

    Write-Host 'WinFsp install finished.' -ForegroundColor Green
    Invoke-CheckScript | Out-Null
    Wait-InteractiveExit
    exit 0
}
catch {
    Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
    Wait-InteractiveExit
    exit 1
}