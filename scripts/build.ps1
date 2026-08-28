[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$RevitInstallDir,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'JarviTools.csproj'
$bridgeProject = Join-Path $repoRoot 'bridge\RevitMcpBridge\RevitMcpBridge.csproj'

function Resolve-RevitInstallDir {
    param([string]$Requested)

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($Requested) { $candidates.Add($Requested) }
    if ($env:REVIT_2024_INSTALL_DIR) { $candidates.Add($env:REVIT_2024_INSTALL_DIR) }
    $candidates.Add((Join-Path $env:ProgramW6432 'Autodesk\Revit 2024'))

    Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object {
            $_.PSObject.Properties['DisplayName'] -and
            $_.PSObject.Properties['InstallLocation'] -and
            $_.DisplayName -eq 'Autodesk Revit 2024' -and
            $_.InstallLocation
        } |
        ForEach-Object {
            $candidates.Add([string]$_.InstallLocation)
            $candidates.Add((Join-Path ([string]$_.InstallLocation) 'Revit 2024'))
        }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ($candidate -and
            (Test-Path -LiteralPath (Join-Path $candidate 'RevitAPI.dll')) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'RevitAPIUI.dll'))) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Revit 2024 API not found. Pass -RevitInstallDir or set REVIT_2024_INSTALL_DIR.'
}

function Resolve-MSBuild {
    $known = @(
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe')
    )
    foreach ($item in $known) {
        if ($item -and (Test-Path -LiteralPath $item)) { return $item }
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
            Select-Object -First 1
        if ($found) { return $found }
    }

    throw 'MSBuild 17 was not found. Install Visual Studio 2022 or Build Tools 2022.'
}

$resolvedRevitDir = Resolve-RevitInstallDir -Requested $RevitInstallDir
$msbuild = Resolve-MSBuild

$arguments = @(
    $projectPath,
    '/t:Rebuild',
    "/p:Configuration=$Configuration",
    '/p:Platform=AnyCPU',
    "/p:RevitInstallDir=$resolvedRevitDir"
)
if ($OutDir) {
    $resolvedOutDir = [System.IO.Path]::GetFullPath($OutDir).TrimEnd('\')
    $arguments += "/p:OutDir=$resolvedOutDir"
}

Write-Host "Building OpenRevit Tools against $resolvedRevitDir"
& $msbuild @arguments
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed with exit code $LASTEXITCODE." }

Write-Host 'Building the MCP stdio bridge'
$bridgeArguments = @('build', $bridgeProject, '-c', $Configuration, '--nologo')
if ($OutDir) {
    $bridgeTarget = Join-Path ([System.IO.Path]::GetFullPath($OutDir)) 'bridge'
    $bridgeArguments += @('-o', $bridgeTarget)
}
& dotnet @bridgeArguments
if ($LASTEXITCODE -ne 0) { throw "Bridge build failed with exit code $LASTEXITCODE." }

Write-Host 'Build completed.'
