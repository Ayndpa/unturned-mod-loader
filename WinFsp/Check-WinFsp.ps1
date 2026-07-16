#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

function Test-WinFspInstalled {
    $svc = Get-Service -Name 'WinFsp.Launcher' -ErrorAction SilentlyContinue
    if ($svc) {
        return $true
    }

    $dllRoots = @(
        ${env:ProgramFiles(x86)},
        ${env:ProgramFiles}
    ) | Where-Object { $_ }

    foreach ($root in $dllRoots) {
        $dll = Join-Path $root 'WinFsp\bin\winfsp-x64.dll'
        if (Test-Path -LiteralPath $dll) {
            return $true
        }
    }

    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
        foreach ($sub in @('SOFTWARE\WinFsp', 'SOFTWARE\WinFsp.Launcher')) {
            $key = $base.OpenSubKey($sub)
            if ($key) {
                $ver = $key.GetValue('Version')
                if (-not $ver) { $ver = $key.GetValue('InstalledVersion') }
                if ($ver) {
                    $script:DetectedVersion = [string]$ver
                    return $true
                }
            }
        }
    }

    return $false
}

function Get-WinFspVersion {
    if ($script:DetectedVersion) {
        return $script:DetectedVersion
    }
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
        foreach ($sub in @('SOFTWARE\WinFsp', 'SOFTWARE\WinFsp.Launcher')) {
            $key = $base.OpenSubKey($sub)
            if ($key) {
                $ver = $key.GetValue('Version')
                if (-not $ver) { $ver = $key.GetValue('InstalledVersion') }
                if ($ver) { return [string]$ver }
            }
        }
    }
    return ''
}

if (Test-WinFspInstalled) {
    $v = Get-WinFspVersion
    if ($v) {
        Write-Output "INSTALLED:$v"
    } else {
        Write-Output 'INSTALLED'
    }
    exit 0
}

Write-Output 'NOT_INSTALLED'
exit 1