#Requires -Version 5.1
<#
.SYNOPSIS
  将 Unturned Mod Loader 打成 Windows x64 自包含包，并用 Velopack 生成安装器。

.DESCRIPTION
  1) dotnet publish：win-x64 自包含 + 单文件 + 压缩（不裁剪）
  2) vpk pack：生成 Setup.exe / 更新包（无需目标机预装 .NET）

  输出：
  - publish/win-x64/     原始 publish 产物
  - releases/            Velopack 安装器与 nupkg 更新包

.PARAMETER Version
  发行版本（semver）。默认读取 csproj 的 <Version>，否则 1.0.0。

.PARAMETER SkipVpk
  只 publish，不跑 vpk pack。

.PARAMETER PackId
  Velopack 应用 ID（默认 UnturnedModLoader）。

.EXAMPLE
  .\publish-mod-loader.ps1
  .\publish-mod-loader.ps1 -Version 1.0.1
  .\publish-mod-loader.ps1 -SkipVpk
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '',
    [string]$PackId = 'UnturnedModLoader',
    [switch]$SkipVpk
)

$ErrorActionPreference = 'Stop'

$ProjectDir = $PSScriptRoot
$Project = Join-Path $ProjectDir 'UnturnedModLoader.csproj'
$PublishDir = Join-Path $ProjectDir 'publish\win-x64'
$ReleasesDir = Join-Path $ProjectDir 'releases'
$MainExe = 'UnturnedModLoader.exe'

if (-not (Test-Path $Project)) {
    throw "找不到项目: $Project"
}

function Get-ProjectVersion {
    param([string]$CsprojPath)
    try {
        [xml]$xml = Get-Content -LiteralPath $CsprojPath -Raw
        $nodes = $xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }
        if ($nodes) { return [string]$nodes[0] }
    } catch {
        # fall through
    }
    return '1.0.0'
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion -CsprojPath $Project
}

Write-Host '=== Unturned Mod Loader publish + Velopack ===' -ForegroundColor Cyan
Write-Host "  RID           : win-x64"
Write-Host "  Configuration : $Configuration"
Write-Host "  Version       : $Version"
Write-Host "  PackId        : $PackId"
Write-Host "  SelfContained : true"
Write-Host "  Trim          : off"
Write-Host "  Publish dir   : $PublishDir"
Write-Host "  Releases dir  : $ReleasesDir"
Write-Host ''

# 若旧的 publish 产物仍在运行，先结束再清理
Get-Process -Name 'UnturnedModLoader' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

# 清理输出与 win-x64 中间产物，避免增量缓存打出脏包
foreach ($p in @(
        $PublishDir
        (Join-Path $ProjectDir "bin\$Configuration\net10.0\win-x64")
        (Join-Path $ProjectDir "obj\$Configuration\net10.0\win-x64")
    )) {
    if (Test-Path $p) {
        try {
            Remove-Item -Recurse -Force $p -ErrorAction Stop
        } catch {
            throw "无法清理目录（可能程序仍在运行或被占用）: $p`n$_"
        }
    }
}

$publishArgs = @(
    'publish', $Project
    '-c', $Configuration
    '-r', 'win-x64'
    '--self-contained', 'true'
    '--force'
    '-o', $PublishDir
    '-p:SelfContained=true'
    "-p:Version=$Version"
    '-p:PublishSingleFile=true'
    # Keep managed assemblies extractable so third-party libs that read
    # Assembly.Location (notably winfsp-msil CheckVersion) still work.
    '-p:IncludeAllContentForSelfExtract=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:EnableCompressionInSingleFile=true'
    '-p:DebugType=none'
    '-p:DebugSymbols=false'
    '-p:DebuggerSupport=false'
    '-p:IncludeSymbols=false'
    '-p:CopyDebugSymbolFilesFromPackages=false'
    '-p:PublishTrimmed=false'
)

Write-Host '--- dotnet publish ---' -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败 (exit $LASTEXITCODE)"
}

$pdbs = @(Get-ChildItem -Path $PublishDir -Filter '*.pdb' -File -Recurse -ErrorAction SilentlyContinue)
if ($pdbs.Count -gt 0) {
    $pdbBytes = ($pdbs | Measure-Object Length -Sum).Sum
    Write-Host ("清理 PDB: {0} 个, {1:N1} MB" -f $pdbs.Count, ($pdbBytes / 1MB)) -ForegroundColor Yellow
    $pdbs | Remove-Item -Force
}

$publishFiles = @(Get-ChildItem -Path $PublishDir -File -Recurse | Sort-Object Length -Descending)
$publishTotal = if ($publishFiles.Count) { ($publishFiles | Measure-Object Length -Sum).Sum } else { 0 }

Write-Host ''
Write-Host '=== publish 输出 ===' -ForegroundColor Green
foreach ($f in $publishFiles) {
    Write-Host ('  {0,10:N1} MB  {1}' -f ($f.Length / 1MB), $f.Name)
}
Write-Host ("合计: {0:N2} MB" -f ($publishTotal / 1MB)) -ForegroundColor Green

$exePath = Join-Path $PublishDir $MainExe
if (-not (Test-Path $exePath)) {
    throw "找不到主程序: $exePath"
}

if ($SkipVpk) {
    Write-Host ''
    Write-Host '已跳过 Velopack（-SkipVpk）。' -ForegroundColor Yellow
    Write-Host "目录: $PublishDir"
    return
}

function Resolve-Vpk {
    $cmd = Get-Command vpk -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    Write-Host ''
    Write-Host '未找到 vpk，正在安装全局工具 (dotnet tool install -g vpk)...' -ForegroundColor Yellow
    & dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) {
        # 可能已安装但 PATH 未刷新
        & dotnet tool update -g vpk
    }

    $cmd = Get-Command vpk -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $userTools = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
    if (Test-Path $userTools) { return $userTools }

    throw "无法找到 vpk。请手动执行: dotnet tool install -g vpk ，并确保 %USERPROFILE%\.dotnet\tools 在 PATH 中。"
}

$vpk = Resolve-Vpk
Write-Host ''
Write-Host "--- vpk pack ($vpk) ---" -ForegroundColor Cyan

if (-not (Test-Path $ReleasesDir)) {
    New-Item -ItemType Directory -Path $ReleasesDir | Out-Null
}

# packDir 指向 publish 输出；Velopack 生成 Setup + 更新 nupkg
& $vpk pack `
    --packId $PackId `
    --packVersion $Version `
    --packDir $PublishDir `
    --mainExe $MainExe `
    --packTitle 'Unturned Mod Loader' `
    --outputDir $ReleasesDir

if ($LASTEXITCODE -ne 0) {
    throw "vpk pack 失败 (exit $LASTEXITCODE)"
}

$releaseFiles = @(Get-ChildItem -Path $ReleasesDir -File -Recurse | Sort-Object Length -Descending)
$releaseTotal = if ($releaseFiles.Count) { ($releaseFiles | Measure-Object Length -Sum).Sum } else { 0 }

Write-Host ''
Write-Host '=== Velopack releases ===' -ForegroundColor Green
foreach ($f in $releaseFiles) {
    Write-Host ('  {0,10:N1} MB  {1}' -f ($f.Length / 1MB), $f.Name)
}
Write-Host ("合计: {0:N2} MB" -f ($releaseTotal / 1MB)) -ForegroundColor Green
Write-Host ''
Write-Host "安装器目录: $ReleasesDir" -ForegroundColor Green
Write-Host '用户运行 *Setup.exe 即可安装；后续可把 releases 托管到 HTTP 做自动更新。' -ForegroundColor DarkGray
Write-Host '应用内更新需再接入 UpdateManager（当前仅完成安装器打包 + 启动引导）。' -ForegroundColor DarkGray
