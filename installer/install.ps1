param(
    [string]$InstallDir = "$env:LOCALAPPDATA\ZwcadBatchPlot\Plugin"
)

$ErrorActionPreference = "Stop"
$appName = "ZwcadBatchPlot"
$description = "ZWCAD Batch Plot Plugin"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDll = Join-Path $scriptDir "BatchPlotter.dll"
$zwcadRoot = "HKCU:\Software\ZWSOFT\ZWCAD"
$trustedFolder = $InstallDir.TrimEnd('\') + "\..."

if (!(Test-Path $sourceDll)) {
    throw "未找到 BatchPlotter.dll。请在解压后的完整安装包目录中运行本脚本。"
}

<#
.SYNOPSIS
检测当前进程是否以管理员令牌运行。
.DESCRIPTION
UAC 下“以管理员身份运行”会装到管理员配置，普通用户打开中望看不到插件。
#>
function Test-IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

<#
.SYNOPSIS
检测会锁定插件 DLL 的中望进程。
#>
function Get-RunningZwcadProcesses {
    $names = @("zwcad")
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
判断是否为中望CAD语言配置键（zh-CN / en-US 等），排除 PluginManager、ZRX。
#>
function Test-ZwcadLocaleKey {
    param(
        [string]$Name,
        [string]$KeyPath
    )

    if ($Name -eq "PluginManager" -or $Name -eq "ZRX") {
        return $false
    }

    if ($Name -match "^[a-z]{2}(-[A-Za-z]{2,})?$") {
        return $true
    }

    return Test-Path (Join-Path $KeyPath "Profiles")
}

<#
.SYNOPSIS
收集 DemandLoad 用的 Applications 路径。
.DESCRIPTION
仅注册 2025 及以后的年份版本；即使 PluginManager 下已有 Applications 也不写入，避免双加载。
#>
function Get-ZwcadApplicationRoots {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProductRoot
    )

    $roots = New-Object System.Collections.Generic.List[string]
    Get-ChildItem $ProductRoot | ForEach-Object {
        $versionName = $_.PSChildName
        if ($versionName -notmatch "^\d{4}$" -or [int]$versionName -lt 2025) {
            return
        }

        $versionKey = $_.PSPath
        Get-ChildItem $versionKey | ForEach-Object {
            if ($_.PSChildName -eq "PluginManager" -or $_.PSChildName -eq "ZRX") {
                return
            }

            $apps = Join-Path $_.PSPath "Applications"
            if (Test-Path $apps) {
                $roots.Add($apps)
                return
            }

            if (Test-ZwcadLocaleKey -Name $_.PSChildName -KeyPath $_.PSPath) {
                New-Item -Force -Path $apps | Out-Null
                $roots.Add($apps)
            }
        }
    }

    return @($roots | Select-Object -Unique)
}

<#
.SYNOPSIS
把插件目录追加到中望配置的 TRUSTEDPATHS，不覆盖原有路径，不修改 SECURELOAD。
#>
function Add-ZwcadTrustedPath {
    param(
        [Parameter(Mandatory = $true)][string]$ProductRoot,
        [Parameter(Mandatory = $true)][string]$FolderToTrust
    )

    Get-ChildItem $ProductRoot -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.PSChildName -notmatch "^\d{4}$" -or [int]$_.PSChildName -lt 2025) {
            return
        }

        Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.PSChildName -eq "PluginManager" -or $_.PSChildName -eq "ZRX") {
                return
            }

            $profiles = Join-Path $_.PSPath "Profiles"
            if (!(Test-Path $profiles)) {
                return
            }

            Get-ChildItem $profiles -ErrorAction SilentlyContinue | ForEach-Object {
                $variables = Join-Path $_.PSPath "Variables"
                if (Test-Path $variables) {
                    Add-TrustedPathValue -VariablesPath $variables -FolderToTrust $FolderToTrust
                }
            }
        }
    }
}

<#
.SYNOPSIS
向 Variables\TRUSTEDPATHS 追加一个以 \... 结尾的目录。
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

if (Test-IsElevated) {
    throw "请不要以管理员身份运行安装。关闭本窗口后，在资源管理器中直接双击 安装.cmd。"
}

$running = @(Get-RunningZwcadProcesses)
if ($running.Count -gt 0) {
    $names = ($running | ForEach-Object { $_.ProcessName + " (" + $_.Id + ")" }) -join ", "
    throw "中望CAD 仍在运行（$names），插件文件会被占用。请先退出中望后再安装。"
}

if (!(Test-Path $zwcadRoot)) {
    throw "未找到中望CAD注册表。请先启动一次中望CAD 2025 或更新版本，再运行安装。"
}

Unblock-Tree -Path $scriptDir
Sync-PluginDirectory -Source $scriptDir -Destination $InstallDir
Unblock-Tree -Path $InstallDir

$loader = Join-Path $InstallDir "BatchPlotter.dll"
$applicationRoots = @(Get-ZwcadApplicationRoots -ProductRoot $zwcadRoot)
if ($applicationRoots.Count -eq 0) {
    throw "未找到中望CAD 2025 及以后版本的注册表。请先启动一次对应版本的中望CAD，再运行安装。"
}

foreach ($root in $applicationRoots) {
    $key = Join-Path $root $appName
    New-Item -Force -Path $key | Out-Null
    New-ItemProperty -Force -Path $key -Name "DESCRIPTION" -Value $description -PropertyType String | Out-Null
    New-ItemProperty -Force -Path $key -Name "LOADCTRLS" -Value 2 -PropertyType DWord | Out-Null
    New-ItemProperty -Force -Path $key -Name "LOADER" -Value $loader -PropertyType String | Out-Null
    New-ItemProperty -Force -Path $key -Name "MANAGED" -Value 1 -PropertyType DWord | Out-Null
}

Add-ZwcadTrustedPath -ProductRoot $zwcadRoot -FolderToTrust $trustedFolder

Write-Host ""
Write-Host "安装完成。" -ForegroundColor Green
Write-Host "插件文件: $loader"
if (Test-Path (Join-Path $InstallDir "Plotters\LA_pdf.pc5")) {
    Write-Host "已复制绘图仪: LA_pdf.pc5 / LA_pdf.pmp"
}
Write-Host "已写入当前用户下中望CAD 2025 及以后版本的自动加载。"
Write-Host "下次启动中望CAD 将自动加载本插件。"
Write-Host ""
