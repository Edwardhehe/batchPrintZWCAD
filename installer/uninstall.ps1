param(
    [string]$InstallDir = "$env:LOCALAPPDATA\ZwcadBatchPlot\Plugin"
)

$ErrorActionPreference = "Stop"
$appName = "ZwcadBatchPlot"
$removed = 0
$zwcadRoot = "HKCU:\Software\ZWSOFT\ZWCAD"

if (Test-Path $zwcadRoot) {
    Get-ChildItem $zwcadRoot | ForEach-Object {
        $versionKey = $_.PSPath
        Get-ChildItem $versionKey | ForEach-Object {
            $apps = Join-Path $_.PSPath "Applications"
            $key = Join-Path $apps $appName
            if (Test-Path $key) {
                Remove-Item -Recurse -Force $key
                $removed++
            }
        }
    }
}

$deletedFiles = $false
if (Test-Path $InstallDir) {
    try {
        Remove-Item -Recurse -Force $InstallDir
        $deletedFiles = $true
    }
    catch {
        Write-Host "Plugin files could not be deleted. ZWCAD may still be running." -ForegroundColor Yellow
        Write-Host "Close ZWCAD and delete this folder manually if needed:"
        Write-Host $InstallDir
    }
}

Write-Host ""
Write-Host "Uninstall completed." -ForegroundColor Green
Write-Host "Autoload entries removed: $removed"
if ($deletedFiles) {
    Write-Host "Plugin files deleted: $InstallDir"
}
Write-Host "User data is kept in AppData\\Roaming\\ZwcadBatchPlot."
Write-Host ""
Read-Host "Press Enter to exit"
