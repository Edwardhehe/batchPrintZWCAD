param(
    [string]$InstallDir = "$env:LOCALAPPDATA\AcadBatchPlot\Plugin"
)

$ErrorActionPreference = "Stop"
$appName = "AcadBatchPlot"
$removed = 0
$acadRoot = "HKCU:\Software\Autodesk\AutoCAD"
$bundlePath = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\AcadBatchPlot.bundle"
$pluginTrusted = $InstallDir.TrimEnd('\') + "\..."
$bundleTrusted = $bundlePath.TrimEnd('\') + "\..."

<#
.SYNOPSIS
从 Variables\TRUSTEDPATHS 中移除本插件目录。
#>
function Remove-TrustedPathValue {
    param(
        [Parameter(Mandatory = $true)][string]$VariablesPath,
        [Parameter(Mandatory = $true)][string]$FolderToTrust
    )

    $item = Get-ItemProperty -Path $VariablesPath -Name TRUSTEDPATHS -ErrorAction SilentlyContinue
    if (!($item -and $null -ne $item.TRUSTEDPATHS)) {
        return
    }

    $current = [string]$item.TRUSTEDPATHS
    $parts = @($current -split ";" | ForEach-Object { $_.Trim() } | Where-Object {
        $_ -and -not [string]::Equals($_.TrimEnd('\'), $FolderToTrust.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
    })
    New-ItemProperty -Force -Path $VariablesPath -Name TRUSTEDPATHS -Value ($parts -join ";") -PropertyType String | Out-Null
}

$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    @("acad", "accoreconsole") -contains $_.ProcessName.ToLowerInvariant()
})
if ($running.Count -gt 0) {
    throw "AutoCAD 仍在运行，无法卸载插件文件。请先退出 AutoCAD 后再卸载。"
}

if (Test-Path $acadRoot) {
    Get-ChildItem $acadRoot | Where-Object { $_.PSChildName -like "R*" } | ForEach-Object {
        Get-ChildItem $_.PSPath | Where-Object { $_.PSChildName -like "ACAD-*" } | ForEach-Object {
            $apps = Join-Path $_.PSPath "Applications"
            $key = Join-Path $apps $appName
            if (Test-Path $key) {
                Remove-Item -Recurse -Force $key
                $removed++
            }

            $profiles = Join-Path $_.PSPath "Profiles"
            if (Test-Path $profiles) {
                Get-ChildItem $profiles -ErrorAction SilentlyContinue | ForEach-Object {
                    $variables = Join-Path $_.PSPath "Variables"
                    if (Test-Path $variables) {
                        Remove-TrustedPathValue -VariablesPath $variables -FolderToTrust $pluginTrusted
                        Remove-TrustedPathValue -VariablesPath $variables -FolderToTrust $bundleTrusted
                    }
                }
            }
        }
    }
}

$deletedBundle = $false
if (Test-Path $bundlePath) {
    Remove-Item -Recurse -Force $bundlePath
    $deletedBundle = -not (Test-Path $bundlePath)
    if ($deletedBundle) {
        $removed++
    }
}

$deletedFiles = $false
if (Test-Path $InstallDir) {
    Remove-Item -Recurse -Force $InstallDir
    $deletedFiles = -not (Test-Path $InstallDir)
}

Write-Host ""
if ($deletedFiles -or $deletedBundle -or $removed -gt 0) {
    Write-Host "卸载完成。" -ForegroundColor Green
}
else {
    Write-Host "未找到已安装的自动加载项或插件文件。" -ForegroundColor Yellow
}
Write-Host "已删除自动加载项: $removed"
if ($deletedBundle) {
    Write-Host "已删除 Bundle: $bundlePath"
}
if ($deletedFiles) {
    Write-Host "已删除插件文件: $InstallDir"
}
Write-Host "用户数据仍保留在 AppData\Roaming\AcadBatchPlot。"
Write-Host ""
