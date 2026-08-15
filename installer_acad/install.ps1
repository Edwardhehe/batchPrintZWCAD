param(
    [string]$InstallDir = "$env:LOCALAPPDATA\AcadBatchPlot\Plugin"
)

$ErrorActionPreference = "Stop"
$appName = "AcadBatchPlot"
$description = "AutoCAD Batch Plot Plugin"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageName = Split-Path -Leaf $scriptDir
$sourceDll = Join-Path $scriptDir "AcadBatchPlot.dll"
if (!(Test-Path $sourceDll)) {
    $sourceDll = Join-Path $scriptDir "AcadBatchPlot.Core.dll"
}

if (!(Test-Path $sourceDll)) {
    throw "AcadBatchPlot.dll or AcadBatchPlot.Core.dll was not found. Please run this script from the release package folder."
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

Get-ChildItem -Path (Join-Path $scriptDir "*") -File -Include *.dll,*.pdb,*.json,*.config | ForEach-Object {
    Copy-Item -Force -Path $_.FullName -Destination $InstallDir
}

$runtimeDir = Join-Path $scriptDir "runtimes"
if (Test-Path $runtimeDir) {
    Copy-Item -Recurse -Force -Path $runtimeDir -Destination $InstallDir
}

$plottersDir = Join-Path $scriptDir "Plotters"
if (Test-Path $plottersDir) {
    Copy-Item -Recurse -Force -Path $plottersDir -Destination $InstallDir
}

$loader = Join-Path $InstallDir (Split-Path $sourceDll -Leaf)
$isCorePackage = (Split-Path $sourceDll -Leaf) -eq "AcadBatchPlot.Core.dll"

function Remove-AcadBatchPlotRegistryAutoload {
    $acadRoot = "HKCU:\Software\Autodesk\AutoCAD"
    if (!(Test-Path $acadRoot)) {
        return
    }

    Get-ChildItem $acadRoot | Where-Object { $_.PSChildName -like "R*" } | ForEach-Object {
        $versionKey = $_.PSPath
        Get-ChildItem $versionKey | Where-Object { $_.PSChildName -like "ACAD-*" } | ForEach-Object {
            $appKey = Join-Path (Join-Path $_.PSPath "Applications") $appName
            if (Test-Path $appKey) {
                Remove-Item -Recurse -Force -Path $appKey
            }
        }
    }
}

function Install-CoreBundle {
    $bundlePath = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\AcadBatchPlot.bundle"
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($loader).FileVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        $version = "0.0.0.0"
    }

    $installFolderName = "v$version-$(Get-Date -Format 'yyyyMMddHHmmssfff')"
    $contentsRoot = Join-Path $bundlePath "Contents"
    $contentsPath = Join-Path $contentsRoot $installFolderName
    New-Item -ItemType Directory -Force -Path $contentsPath | Out-Null

    Get-ChildItem -Path (Join-Path $InstallDir "*") -File -Include *.dll,*.pdb,*.json,*.config | ForEach-Object {
        Copy-Item -Force -Path $_.FullName -Destination $contentsPath
    }

    $installedRuntimeDir = Join-Path $InstallDir "runtimes"
    if (Test-Path $installedRuntimeDir) {
        Copy-Item -Recurse -Force -Path $installedRuntimeDir -Destination $contentsPath
    }

    $installedPlottersDir = Join-Path $InstallDir "Plotters"
    if (Test-Path $installedPlottersDir) {
        Copy-Item -Recurse -Force -Path $installedPlottersDir -Destination $contentsPath
    }

    $dllName = Split-Path $loader -Leaf
    $packageContents = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" Name="LA批量打印" AppVersion="1.15.2" ProductCode="{7f2f2f2d-78d1-4df0-8c5d-acadba7c0011}" Description="AutoCAD批量打印插件">
  <CompanyDetails Name="lihao" />
  <Components>
    <ComponentEntry AppName="AcadBatchPlot" AppType=".Net" ModuleName="./Contents/$installFolderName/$dllName" LoadOnAutoCADStartup="True" LoadOnCommandInvocation="True">
      <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R25.0" />
      <Commands GroupName="AcadBatchPlot">
        <Command Global="ZBP_SHOW_PANEL" Local="ZBP_SHOW_PANEL" />
        <Command Global="ZBP_RECTANGLE_BATCH_PLOT" Local="ZBP_RECTANGLE_BATCH_PLOT" />
        <Command Global="ZBP_SINGLE_PLOT" Local="ZBP_SINGLE_PLOT" />
        <Command Global="ZBP_ADD_TITLE_BLOCK" Local="ZBP_ADD_TITLE_BLOCK" />
        <Command Global="ZBP_MANAGE_LIBRARY" Local="ZBP_MANAGE_LIBRARY" />
        <Command Global="ZBP_SETTINGS" Local="ZBP_SETTINGS" />
        <Command Global="ZBP_OPEN_CONFIG" Local="ZBP_OPEN_CONFIG" />
        <Command Global="ZBP_RELOAD_MENU" Local="ZBP_RELOAD_MENU" />
      </Commands>
    </ComponentEntry>
    <ComponentEntry AppName="AcadBatchPlotMenu" AppType="Mnu" ModuleName="./Contents/$installFolderName/AcadBatchPlot.mnu">
      <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R25.0" />
    </ComponentEntry>
  </Components>
</ApplicationPackage>
"@

    $menuContents = @"
***MENUGROUP=ACADBATCHPLOT
***POP1
**LA_BATCH_PLOT
ID_LA_BATCH_PLOT [LA批量打印]
ID_ZBP_ADD_TITLE_BLOCK [新增图框]ZBP_ADD_TITLE_BLOCK
ID_ZBP_MANAGE_LIBRARY [图框库管理]ZBP_MANAGE_LIBRARY
ID_ZBP_SHOW_PANEL [批量打印(选图框块)]ZBP_SHOW_PANEL
ID_ZBP_RECTANGLE_BATCH_PLOT [批量打印(选矩形框)]ZBP_RECTANGLE_BATCH_PLOT
ID_ZBP_SINGLE_PLOT [单张打印]ZBP_SINGLE_PLOT
[--]
ID_ZBP_SETTINGS [设置]ZBP_SETTINGS
ID_ZBP_UNINSTALL_AUTOLOAD [卸载自动加载]ZBP_UNINSTALL_AUTOLOAD
ID_ZBP_OPEN_CONFIG [打开配置目录]ZBP_OPEN_CONFIG
ID_ZBP_RELOAD_MENU [刷新菜单]ZBP_RELOAD_MENU
"@

    [System.IO.File]::WriteAllText((Join-Path $bundlePath "PackageContents.xml"), $packageContents, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText((Join-Path $contentsPath "AcadBatchPlot.mnu"), $menuContents, [System.Text.Encoding]::UTF8)
    Remove-AcadBatchPlotRegistryAutoload
    return $bundlePath
}

if ($isCorePackage) {
    $bundlePath = Install-CoreBundle
    Write-Host ""
    Write-Host "Install completed." -ForegroundColor Green
    Write-Host "Plugin file: $loader"
    Write-Host "Autoloader bundle: $bundlePath"
    if (Test-Path $plottersDir) {
        Write-Host "Bundled plotter copied: LA_pdf.pc3 / LA_pdf.pmp"
    }
    Write-Host "AutoCAD 2025-2027 will load this plugin from the ApplicationPlugins bundle next time."
    Write-Host ""
    exit
}

function Get-AcadMajorVersion([string]$registryName) {
    if ($registryName -match '^R(\d+)') {
        return [int]$Matches[1]
    }

    return $null
}

function Test-CompatiblePackage([string]$registryName) {
    $major = Get-AcadMajorVersion $registryName
    if ($null -eq $major) {
        return $false
    }

    if ($isCorePackage) {
        return $major -ge 25
    }

    if ($packageName -match '2019(?:-2020)?') {
        return $major -eq 23
    }

    if ($packageName -match '2021(?:-2024)?') {
        return $major -eq 24
    }

    if ($packageName -match '2025(?:Plus)?') {
        return $major -ge 25
    }

    return $major -lt 25
}

$acadRoot = "HKCU:\Software\Autodesk\AutoCAD"
if (!(Test-Path $acadRoot)) {
    throw "AutoCAD registry key was not found. Please start AutoCAD once, then run installer again."
}

$applicationRoots = @()
Get-ChildItem $acadRoot | Where-Object { $_.PSChildName -like "R*" } | ForEach-Object {
    if (!(Test-CompatiblePackage $_.PSChildName)) {
        return
    }

    $versionKey = $_.PSPath
    Get-ChildItem $versionKey | Where-Object { $_.PSChildName -like "ACAD-*" } | ForEach-Object {
        $apps = Join-Path $_.PSPath "Applications"
        if (Test-Path $apps) {
            $applicationRoots += $apps
        }
    }
}

if ($applicationRoots.Count -eq 0) {
    throw "Compatible AutoCAD Applications registry key was not found. Please use the package matching your AutoCAD version, start AutoCAD once, then run installer again."
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
if (Test-Path $plottersDir) {
    Write-Host "Bundled plotter copied: LA_pdf.pc3 / LA_pdf.pmp"
}
Write-Host "AutoCAD will load this plugin automatically next time."
Write-Host ""
