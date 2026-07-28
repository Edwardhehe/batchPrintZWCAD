param(
    [string]$InstallDir = "$env:LOCALAPPDATA\AcadBatchPlot\Plugin"
)

$ErrorActionPreference = "Stop"
$appName = "AcadBatchPlot"
$removed = 0
$acadRoot = "HKCU:\Software\Autodesk\AutoCAD"

if (Test-Path $acadRoot) {
    Get-ChildItem $acadRoot | Where-Object { $_.PSChildName -like "R*" } | ForEach-Object {
        $versionKey = $_.PSPath
        Get-ChildItem $versionKey | Where-Object { $_.PSChildName -like "ACAD-*" } | ForEach-Object {
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
        Write-Host "Plugin files could not be deleted. AutoCAD may still be running." -ForegroundColor Yellow
        Write-Host "Close AutoCAD and delete this folder manually if needed:"
        Write-Host $InstallDir
    }
}

Write-Host ""
Write-Host "Uninstall completed." -ForegroundColor Green
Write-Host "Autoload entries removed: $removed"
if ($deletedFiles) {
    Write-Host "Plugin files deleted: $InstallDir"
}
Write-Host "User data is kept in AppData\Roaming\AcadBatchPlot."
Write-Host ""
