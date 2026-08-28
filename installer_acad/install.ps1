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
    throw "未找到 AcadBatchPlot.dll 或 AcadBatchPlot.Core.dll。请在解压后的完整安装包目录中运行本脚本。"
}

$loader = Join-Path $InstallDir (Split-Path $sourceDll -Leaf)
$isCorePackage = (Split-Path $sourceDll -Leaf) -eq "AcadBatchPlot.Core.dll"
$bundlePath = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\AcadBatchPlot.bundle"
$pluginTrusted = $InstallDir.TrimEnd('\') + "\..."
$bundleTrusted = $bundlePath.TrimEnd('\') + "\..."
$acadRoot = "HKCU:\Software\Autodesk\AutoCAD"

<#
.SYNOPSIS
检测当前进程是否以管理员令牌运行。
#>
function Test-IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

<#
.SYNOPSIS
检测会锁定插件 DLL 的 AutoCAD 进程。
#>
function Get-RunningAcadProcesses {
    $names = @("acad", "accoreconsole")
    Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $names -contains $_.ProcessName.ToLowerInvariant()
    }
}

<#
.SYNOPSIS
清除 Mark of the Web（Zone.Identifier），避免 .NET 报 0x80131515 无法加载。
#>
function Unblock-Tree {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (!(Test-Path -LiteralPath $Path)) {
        return
    }

    Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
        Unblock-File -ErrorAction SilentlyContinue
}

<#
.SYNOPSIS
把源目录中的插件文件同步到目标目录，并删掉目标里多出来的旧 DLL。
#>
function Sync-PluginDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    $copyExtensions = @(".dll", ".pdb", ".json", ".config")
    Get-ChildItem -LiteralPath $Destination -File -ErrorAction SilentlyContinue | Where-Object {
        $copyExtensions -contains $_.Extension.ToLowerInvariant()
    } | ForEach-Object {
        if (!(Test-Path -LiteralPath (Join-Path $Source $_.Name))) {
            Remove-Item -LiteralPath $_.FullName -Force
        }
    }

    Get-ChildItem -LiteralPath $Source -File -ErrorAction SilentlyContinue | Where-Object {
        $copyExtensions -contains $_.Extension.ToLowerInvariant()
    } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Force
    }

    foreach ($folderName in @("runtimes", "Plotters")) {
        $destFolder = Join-Path $Destination $folderName
        $sourceFolder = Join-Path $Source $folderName
        if (Test-Path -LiteralPath $destFolder) {
            Remove-Item -LiteralPath $destFolder -Recurse -Force
        }
        if (Test-Path -LiteralPath $sourceFolder) {
            Copy-Item -LiteralPath $sourceFolder -Destination $destFolder -Recurse -Force
        }
    }
}

<#
.SYNOPSIS
向 Variables\TRUSTEDPATHS 追加目录，不覆盖原有路径，不修改 SECURELOAD。
#>
function Add-TrustedPathValue {
    param(
        [Parameter(Mandatory = $true)][string]$VariablesPath,
        [Parameter(Mandatory = $true)][string]$FolderToTrust
    )

    $item = Get-ItemProperty -Path $VariablesPath -Name TRUSTEDPATHS -ErrorAction SilentlyContinue
    $current = ""
    if ($item -and $null -ne $item.TRUSTEDPATHS) {
        $current = [string]$item.TRUSTEDPATHS
    }

    if ($current -eq ".") {
        $current = ""
    }

    $parts = @($current -split ";" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($part in $parts) {
        if ([string]::Equals($part.TrimEnd('\'), $FolderToTrust.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }

    $parts += $FolderToTrust
    New-ItemProperty -Force -Path $VariablesPath -Name TRUSTEDPATHS -Value ($parts -join ";") -PropertyType String | Out-Null
}

<#
.SYNOPSIS
给所有已存在的 AutoCAD 用户配置追加 TRUSTEDPATHS。
#>
function Add-AcadTrustedPaths {
    param([string[]]$FoldersToTrust)

    if (!(Test-Path $acadRoot)) {
        return
    }

    Get-ChildItem $acadRoot | Where-Object { $_.PSChildName -like "R*" } | ForEach-Object {
        Get-ChildItem $_.PSPath | Where-Object { $_.PSChildName -like "ACAD-*" } | ForEach-Object {
            $profiles = Join-Path $_.PSPath "Profiles"
            if (!(Test-Path $profiles)) {
                return
            }

            Get-ChildItem $profiles -ErrorAction SilentlyContinue | ForEach-Object {
                $variables = Join-Path $_.PSPath "Variables"
                if (!(Test-Path $variables)) {
                    return
                }

                foreach ($folder in $FoldersToTrust) {
                    Add-TrustedPathValue -VariablesPath $variables -FolderToTrust $folder
                }
            }
        }
    }
}

function Remove-AcadBatchPlotRegistryAutoload {
    if (!(Test-Path $acadRoot)) {
        return
    }

    Get-ChildItem $acadRoot | Where-Object { $_.PSChildName -like "R*" } | ForEach-Object {
        Get-ChildItem $_.PSPath | Where-Object { $_.PSChildName -like "ACAD-*" } | ForEach-Object {
            $appKey = Join-Path (Join-Path $_.PSPath "Applications") $appName
            if (Test-Path $appKey) {
                Remove-Item -Recurse -Force -Path $appKey
            }
        }
    }
}

function Install-CoreBundle {
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($loader).FileVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        $version = "0.0.0.0"
    }

    $installFolderName = "v$version-$(Get-Date -Format 'yyyyMMddHHmmssfff')"
    $contentsRoot = Join-Path $bundlePath "Contents"
    $contentsPath = Join-Path $contentsRoot $installFolderName
    New-Item -ItemType Directory -Force -Path $contentsPath | Out-Null
    Sync-PluginDirectory -Source $InstallDir -Destination $contentsPath
    Unblock-Tree -Path $contentsPath

    $dllName = Split-Path $loader -Leaf
    $packageContents = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" Name="LA批量打印" AppVersion="1.15.6.1" ProductCode="{7f2f2f2d-78d1-4df0-8c5d-acadba7c0011}" Description="AutoCAD批量打印插件">
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

    New-Item -ItemType Directory -Force -Path $bundlePath | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $bundlePath "PackageContents.xml"), $packageContents, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText((Join-Path $contentsPath "AcadBatchPlot.mnu"), $menuContents, [System.Text.Encoding]::UTF8)

    if (Test-Path $contentsRoot) {
        Get-ChildItem -LiteralPath $contentsRoot -Directory -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -ne $installFolderName
        } | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
    }

    Remove-AcadBatchPlotRegistryAutoload
    return $bundlePath
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

    if ($packageName -match '2015-2024') {
        return $major -lt 25
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

<#
.SYNOPSIS
收集 DemandLoad 用的 Applications 路径。
.DESCRIPTION
ACAD-* 产品键已存在但尚无 Applications 时主动创建（干净 AutoCAD 常见）。
#>
function Get-AcadApplicationRoots {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProductRoot
    )

    $roots = New-Object System.Collections.Generic.List[string]
    Get-ChildItem $ProductRoot | Where-Object { $_.PSChildName -like "R*" } | ForEach-Object {
        if (!(Test-CompatiblePackage $_.PSChildName)) {
            return
        }

        $versionKey = $_.PSPath
        Get-ChildItem $versionKey | Where-Object { $_.PSChildName -like "ACAD-*" } | ForEach-Object {
            $apps = Join-Path $_.PSPath "Applications"
            if (Test-Path $apps) {
                $roots.Add($apps)
                return
            }

            New-Item -Force -Path $apps | Out-Null
            $roots.Add($apps)
        }
    }

    return @($roots | Select-Object -Unique)
}

if (Test-IsElevated) {
    throw "请不要以管理员身份运行安装。关闭本窗口后，在资源管理器中直接双击 安装.cmd。"
}

$running = @(Get-RunningAcadProcesses)
if ($running.Count -gt 0) {
    $names = ($running | ForEach-Object { $_.ProcessName + " (" + $_.Id + ")" }) -join ", "
    throw "AutoCAD 仍在运行（$names），插件文件会被占用。请先退出 AutoCAD 后再安装。"
}

Unblock-Tree -Path $scriptDir
Sync-PluginDirectory -Source $scriptDir -Destination $InstallDir
Unblock-Tree -Path $InstallDir

if ($isCorePackage) {
    $installedBundle = Install-CoreBundle
    Add-AcadTrustedPaths -FoldersToTrust @($pluginTrusted, $bundleTrusted)
    Write-Host ""
    Write-Host "安装完成。" -ForegroundColor Green
    Write-Host "插件文件: $loader"
    Write-Host "自动加载 Bundle: $installedBundle"
    if (Test-Path (Join-Path $scriptDir "Plotters")) {
        Write-Host "已复制绘图仪: LA_pdf.pc3 / LA_pdf.pmp"
    }
    Write-Host "AutoCAD 2025-2027 将从 ApplicationPlugins Bundle 自动加载。"
    Write-Host ""
    exit
}

if (!(Test-Path $acadRoot)) {
    throw "未找到 AutoCAD 注册表。请先启动一次 AutoCAD，再运行安装。"
}

$applicationRoots = @(Get-AcadApplicationRoots -ProductRoot $acadRoot)
if ($applicationRoots.Count -eq 0) {
    throw "未找到与本安装包匹配的 AutoCAD 产品注册表。请使用对应版本的安装包，先启动一次 AutoCAD，再运行安装。"
}

foreach ($root in $applicationRoots) {
    $key = Join-Path $root $appName
    New-Item -Force -Path $key | Out-Null
    New-ItemProperty -Force -Path $key -Name "DESCRIPTION" -Value $description -PropertyType String | Out-Null
    New-ItemProperty -Force -Path $key -Name "LOADCTRLS" -Value 2 -PropertyType DWord | Out-Null
    New-ItemProperty -Force -Path $key -Name "LOADER" -Value $loader -PropertyType String | Out-Null
    New-ItemProperty -Force -Path $key -Name "MANAGED" -Value 1 -PropertyType DWord | Out-Null
}

Add-AcadTrustedPaths -FoldersToTrust @($pluginTrusted)

Write-Host ""
Write-Host "安装完成。" -ForegroundColor Green
Write-Host "插件文件: $loader"
if (Test-Path (Join-Path $scriptDir "Plotters")) {
    Write-Host "已复制绘图仪: LA_pdf.pc3 / LA_pdf.pmp"
}
Write-Host "下次启动 AutoCAD 将自动加载本插件。"
Write-Host ""
