param(
    [string]$InstallDir = "$env:LOCALAPPDATA\ZwcadBatchPlot\Plugin"
)

$ErrorActionPreference = "Stop"
$appName = "ZwcadBatchPlot"
$description = "ZWCAD Batch Plot Plugin"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDll = Join-Path $scriptDir "BatchPlotter.dll"

if (!(Test-Path $sourceDll)) {
    throw "BatchPlotter.dll was not found. Please run this script from the release package folder."
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

Get-ChildItem -Path (Join-Path $scriptDir "*") -File -Include *.dll,*.pdb,*.json,*.config | ForEach-Object {
    Copy-Item -Force -Path $_.FullName -Destination $InstallDir
}

$runtimeDir = Join-Path $scriptDir "runtimes"
if (Test-Path $runtimeDir) {
    Copy-Item -Recurse -Force -Path $runtimeDir -Destination $InstallDir
}

$loader = Join-Path $InstallDir "BatchPlotter.dll"
$zwcadRoot = "HKCU:\Software\ZWSOFT\ZWCAD"
if (!(Test-Path $zwcadRoot)) {
    throw "ZWCAD registry key was not found. Please start ZWCAD once, then run installer again."
}

$applicationRoots = @()
Get-ChildItem $zwcadRoot | ForEach-Object {
    $versionKey = $_.PSPath
    Get-ChildItem $versionKey | ForEach-Object {
        $apps = Join-Path $_.PSPath "Applications"
        if (Test-Path $apps) {
            $applicationRoots += $apps
        }
    }
}

if ($applicationRoots.Count -eq 0) {
    throw "ZWCAD Applications registry key was not found. Please start ZWCAD once, then run installer again."
}

foreach ($root in $applicationRoots) {
    $key = Join-Path $root $appName
    New-Item -Force -Path $key | Out-Null
    New-ItemProperty -Force -Path $key -Name "DESCRIPTION" -Value $description -PropertyType String | Out-Null
    New-ItemProperty -Force -Path $key -Name "LOADCTRLS" -Value 2 -PropertyType DWord | Out-Null
    New-ItemProperty -Force -Path $key -Name "LOADER" -Value $loader -PropertyType String | Out-Null
    New-ItemProperty -Force -Path $key -Name "MANAGED" -Value 1 -PropertyType DWord | Out-Null
}

Write-Host ""
Write-Host "Install completed." -ForegroundColor Green
Write-Host "Plugin file: $loader"
Write-Host "ZWCAD will load this plugin automatically next time."
Write-Host ""
Read-Host "Press Enter to exit"
