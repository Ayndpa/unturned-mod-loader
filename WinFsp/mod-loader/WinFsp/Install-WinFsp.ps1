#Requires -Version 5.1
<#
.SYNOPSIS
  安装或修复 WinFsp 文件系统驱动（用户态 FUSE 兼容层）。

.DESCRIPTION
  需管理员权限。优先使用同目录 winfsp-*.msi；否则尝试下载后静默安装。
#>
[CmdletBinding()]
param(
    [switch]$SkipDownload
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
        Read-Host '按 Enter 关闭此窗口'
    }
}

function Invoke-CheckScript {
    if (-not (Test-Path -LiteralPath $CheckScript)) {
        Write-Warning "找不到检查脚本: $CheckScript"
        return 1
    }
    & $CheckScript
    return $LASTEXITCODE
}

function Get-DownloadableMsiUrl {
    $candidates = @()

    try {
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/winfsp/winfsp/releases/latest' `
            -Headers @{ 'User-Agent' = 'UnturnedModLoader' } -TimeoutSec 90
        $asset = $release.assets | Where-Object {
            $_.name -match '^winfsp-[\d.]+\.msi$' -and $_.name -notmatch 'debug'
        } | Select-Object -First 1
        if ($asset) {
            $candidates += [pscustomobject]@{ Url = $asset.browser_download_url; Name = $asset.name }
        }
    }
    catch {
        Write-Host "GitHub API 不可用: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    # API 限流或失败时的直链回退（与 winfsp/winfsp 当前 release 对齐，可用 gh release view 更新）
    $fallbackAssets = @(
        @{ Tag = 'v2.1'; Name = 'winfsp-2.1.25156.msi' }
        @{ Tag = 'v2.0'; Name = 'winfsp-2.0.23075.msi' }
        @{ Tag = 'v1.12.22339'; Name = 'winfsp-1.12.22339.msi' }
    )
    foreach ($fb in $fallbackAssets) {
        $candidates += [pscustomobject]@{
            Url  = "https://github.com/winfsp/winfsp/releases/download/$($fb.Tag)/$($fb.Name)"
            Name = $fb.Name
        }
    }

    foreach ($item in $candidates) {
        try {
            Invoke-WebRequest -Uri $item.Url -Method Head -UseBasicParsing -TimeoutSec 30 | Out-Null
            return $item
        }
        catch {
            continue
        }
    }

    return $null
}

try {
    if (-not (Test-IsAdministrator)) {
        Write-Host '需要管理员权限。请通过 Mod Loader「一键安装」确认 UAC，或以管理员身份运行本脚本。' -ForegroundColor Red
        Wait-InteractiveExit
        exit 1
    }

    $checkExit = Invoke-CheckScript
    if ($checkExit -eq 0) {
        Write-Host 'WinFsp 已安装。' -ForegroundColor Green
        Invoke-CheckScript | Out-Null
        Wait-InteractiveExit
        exit 0
    }

    $msi = Get-ChildItem -Path $ScriptDir -Filter 'winfsp-*.msi' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $msi -and -not $SkipDownload) {
        Write-Host '未找到本地 MSI，正在下载 WinFsp…' -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null

        $dl = Get-DownloadableMsiUrl
        if (-not $dl) {
            throw '无法解析可用的 winfsp-*.msi 下载地址。'
        }

        $dest = Join-Path $CacheDir $dl.Name
        if (-not (Test-Path -LiteralPath $dest)) {
            Write-Host "下载: $($dl.Url)"
            Invoke-WebRequest -Uri $dl.Url -OutFile $dest -UseBasicParsing -TimeoutSec 600
        }
        $msi = Get-Item -LiteralPath $dest
    }

    if (-not $msi) {
        Write-Host "请将 winfsp-*.msi 放到目录: $ScriptDir" -ForegroundColor Red
        Wait-InteractiveExit
        exit 1
    }

    Write-Host "正在安装: $($msi.FullName)" -ForegroundColor Cyan
    $log = Join-Path $env:TEMP ("winfsp-install-{0}.log" -f [Guid]::NewGuid().ToString('N'))

    $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @(
        '/i', $msi.FullName,
        '/qn', '/norestart',
        '/L*v', $log
    ) -Wait -PassThru -NoNewWindow

    if ($proc.ExitCode -ne 0) {
        Write-Host "msiexec 失败，退出码: $($proc.ExitCode)" -ForegroundColor Red
        Write-Host "安装日志: $log" -ForegroundColor Yellow
        Wait-InteractiveExit
        exit $proc.ExitCode
    }

    Write-Host 'WinFsp 安装完成。' -ForegroundColor Green
    Invoke-CheckScript | Out-Null
    Wait-InteractiveExit
    exit 0
}
catch {
    Write-Host "安装失败: $($_.Exception.Message)" -ForegroundColor Red
    Wait-InteractiveExit
    exit 1
}