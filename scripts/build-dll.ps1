param(
    [ValidateSet("Zwcad", "AutoCAD2019", "AutoCAD2021", "AutoCAD2025", "All")]
    [string]$Target = "Zwcad",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

$projects = [ordered]@{
    Zwcad = @{
        Project = "BatchPlotter.csproj"
        Output = "bin"
        Dll = "BatchPlotter.dll"
    }
    AutoCAD2019 = @{
        Project = "AcadBatchPlot.AutoCAD2019.csproj"
        Output = "bin-acad2019"
        Dll = "AcadBatchPlot.dll"
    }
    AutoCAD2021 = @{
        Project = "AcadBatchPlot.csproj"
        Output = "bin-acad"
        Dll = "AcadBatchPlot.dll"
    }
    AutoCAD2025 = @{
        Project = "AcadBatchPlot.Core.csproj"
        Output = "bin-acad2025*"
        Dll = "AcadBatchPlot.Core.dll"
    }
}

function Resolve-OutputPath {
    param(
        [string]$RootPath,
        [string]$Output
    )

    if ($Output -like "*[*?]*") {
        $match = Get-ChildItem -LiteralPath $RootPath -Directory -Filter $Output -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($match) {
            return $match.FullName
        }
    }

    return (Join-Path $RootPath $Output)
}

$selectedTargets = if ($Target -eq "All") { $projects.Keys } else { @($Target) }

foreach ($name in $selectedTargets) {
    $info = $projects[$name]
    $projectPath = Join-Path $root $info.Project
    $outputPath = Resolve-OutputPath -RootPath $root -Output $info.Output

    if ($Clean -and (Test-Path -LiteralPath $outputPath)) {
        try {
            Remove-Item -LiteralPath $outputPath -Recurse -Force
        }
        catch {
            throw "Cannot clean '$outputPath'. Close CAD if it has loaded the DLL, then run this script again. $($_.Exception.Message)"
        }
    }

    & dotnet build $projectPath -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $name. If CAD has loaded the old DLL, close CAD before rebuilding."
    }

    $outputPath = Resolve-OutputPath -RootPath $root -Output $info.Output
    $dllPath = Join-Path $outputPath $info.Dll
    if (-not (Test-Path -LiteralPath $dllPath)) {
        throw "Build finished but expected DLL was not found: $dllPath"
    }

    Write-Host "[$name] $dllPath"
}
