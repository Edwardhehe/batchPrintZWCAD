param(
    [string]$Version = "1.15.6",
    [string]$ZwcadOutput,
    [string]$LegacyAcadOutput,
    [string]$CoreAcadOutput
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+(?:\.\d+)?$') {
    throw "Version must look like 1.15.6 or 1.16."
}

$root = Split-Path -Parent $PSScriptRoot
$releaseBase = Join-Path $root "release"
$releaseName = "v$Version"
$releaseRoot = Join-Path $releaseBase $releaseName
$stagingRoot = Join-Path $releaseBase ("._{0}-{1}" -f $releaseName, $PID)

function Resolve-BuildOutput {
    param(
        [string]$Candidate,
        [Parameter(Mandatory = $true)][string]$DefaultRelativePath
    )

    # CAD 锁定常规 bin 目录时，允许发布流程显式使用同一仓库内的全新重建输出。
    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return Join-Path $root $DefaultRelativePath
    }

    if ([System.IO.Path]::IsPathRooted($Candidate)) {
        return [System.IO.Path]::GetFullPath($Candidate)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $root $Candidate))
}

if (Test-Path -LiteralPath $releaseRoot) {
    throw "Release already exists: $releaseRoot"
}

New-Item -ItemType Directory -Force -Path $releaseBase | Out-Null
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

function Copy-ReleaseTree {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Build output was not found: $Source"
    }

    $sourceRoot = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    $sourceItems = Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force
    foreach ($sourceItem in $sourceItems) {
        # 发布包保留运行时依赖和绘图仪资源，但不携带调试符号、运行日志或 CAD 崩溃记录。
        if (-not $sourceItem.PSIsContainer -and @('.pdb', '.log', '.err') -contains $sourceItem.Extension) {
            continue
        }

        $relative = $sourceItem.FullName.Substring($sourceRoot.Length).TrimStart('\')
        $target = Join-Path $Destination $relative
        if ($sourceItem.PSIsContainer) {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
        }
        else {
            $targetParent = Split-Path -Parent $target
            New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
            Copy-Item -LiteralPath $sourceItem.FullName -Destination $target -Force
        }
    }
}

function Add-ReleasePackage {
    param(
        [Parameter(Mandatory = $true)][string]$FolderName,
        [Parameter(Mandatory = $true)][string]$ArchiveName,
        [Parameter(Mandatory = $true)][string]$BuildOutput,
        [Parameter(Mandatory = $true)][string]$MainDll,
        [Parameter(Mandatory = $true)][string]$InstallerSource
    )

    $dllPath = Join-Path $BuildOutput $MainDll
    if (-not (Test-Path -LiteralPath $dllPath)) {
        throw "Expected release DLL was not found: $dllPath"
    }

    $packageRoot = Join-Path $stagingRoot $FolderName
    Copy-ReleaseTree -Source $BuildOutput -Destination $packageRoot

    foreach ($fileName in @('install.ps1', 'uninstall.ps1', '安装.cmd', '卸载.cmd', '使用说明.txt')) {
        $sourceFile = Join-Path $InstallerSource $fileName
        if (-not (Test-Path -LiteralPath $sourceFile)) {
            throw "Installer file was not found: $sourceFile"
        }
        Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $packageRoot $fileName) -Force
    }

    $archivePath = Join-Path $stagingRoot $ArchiveName
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
}

try {
    $zwcadOutput = Resolve-BuildOutput -Candidate $ZwcadOutput -DefaultRelativePath 'bin'
    $legacyAcadOutput = Resolve-BuildOutput -Candidate $LegacyAcadOutput -DefaultRelativePath 'bin-acad'
    $coreAcadOutput = Resolve-BuildOutput -Candidate $CoreAcadOutput -DefaultRelativePath 'bin-acad2025-2027'

    Add-ReleasePackage `
        -FolderName 'ZWCAD' `
        -ArchiveName "LA批打印-ZWCAD-v$Version.zip" `
        -BuildOutput $zwcadOutput `
        -MainDll 'BatchPlotter.dll' `
        -InstallerSource (Join-Path $root 'installer')

    # AutoCAD 2015-2024 共用同一个 net48 DLL，只生成一个完整兼容包。
    Add-ReleasePackage `
        -FolderName 'AutoCAD2015-2024' `
        -ArchiveName "LA批打印-AutoCAD2015-2024-v$Version.zip" `
        -BuildOutput $legacyAcadOutput `
        -MainDll 'AcadBatchPlot.dll' `
        -InstallerSource (Join-Path $root 'installer_acad')

    # AutoCAD 2025-2027 共用同一个 .NET 8 Core DLL 和运行时依赖。
    Add-ReleasePackage `
        -FolderName 'AutoCAD2025-2027' `
        -ArchiveName "LA批打印-AutoCAD2025-2027-v$Version.zip" `
        -BuildOutput $coreAcadOutput `
        -MainDll 'AcadBatchPlot.Core.dll' `
        -InstallerSource (Join-Path $root 'installer_acad')

    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination (Join-Path $stagingRoot 'docs') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stagingRoot -Force
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $stagingRoot -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs\软件说明.txt') -Destination $stagingRoot -Force
    Get-ChildItem -LiteralPath $root -File -Filter '*.mp4' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stagingRoot -Force
    }

    $manifestLines = @(
        "BatchPlot release v$Version",
        "GeneratedAt=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
        "",
        "Archives (SHA256):"
    )
    Get-ChildItem -LiteralPath $stagingRoot -File -Filter '*.zip' | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $manifestLines += "$hash  $($_.Name)"
    }
    [System.IO.File]::WriteAllLines(
        (Join-Path $stagingRoot 'release-manifest.txt'),
        $manifestLines,
        [System.Text.UTF8Encoding]::new($false))

    Move-Item -LiteralPath $stagingRoot -Destination $releaseRoot
    Write-Host "Local release created: $releaseRoot" -ForegroundColor Green
}
catch {
    if (Test-Path -LiteralPath $stagingRoot) {
        # 仅清理本脚本创建且位于 release 目录内的唯一临时目录。
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
    throw
}
