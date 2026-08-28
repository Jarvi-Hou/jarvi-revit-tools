[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Build,
    [switch]$IncludeSymbols,
    [string]$AddinRoot,
    [string]$BridgeTarget
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = Split-Path -Parent $PSScriptRoot
$packagePluginOutput = Join-Path $repoRoot 'Plugin'
$packageBridgeOutput = Join-Path $repoRoot 'Bridge'
$isReleasePackage =
    (Test-Path -LiteralPath (Join-Path $packagePluginOutput 'JarviTools.dll')) -and
    (Test-Path -LiteralPath (Join-Path $packageBridgeOutput 'RevitMcpBridge.exe'))

if ($Build -and $isReleasePackage) {
    throw 'The binary release package is already built. Run install.ps1 without -Build.'
}
if ($Build) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }

$pluginOutput = if ($isReleasePackage) {
    $packagePluginOutput
} else {
    Join-Path $repoRoot ("bin\{0}" -f $Configuration)
}
$bridgeOutput = if ($isReleasePackage) {
    $packageBridgeOutput
} else {
    Join-Path $repoRoot ("bridge\RevitMcpBridge\bin\{0}\net48" -f $Configuration)
}
$pluginDll = Join-Path $pluginOutput 'JarviTools.dll'
$bridgeExe = Join-Path $bridgeOutput 'RevitMcpBridge.exe'
if (!(Test-Path -LiteralPath $pluginDll)) { throw "Plugin output not found: $pluginDll. Run scripts\build.ps1 first." }
if (!(Test-Path -LiteralPath $bridgeExe)) { throw "Bridge output not found: $bridgeExe. Run scripts\build.ps1 first." }

$addinRoot = if ($AddinRoot) {
    [System.IO.Path]::GetFullPath($AddinRoot)
} else {
    Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024'
}
$pluginTarget = Join-Path $addinRoot 'OpenRevitTools'
$manifestTarget = Join-Path $addinRoot 'OpenRevitTools.addin'
$bridgeTargetPath = if ($BridgeTarget) {
    [System.IO.Path]::GetFullPath($BridgeTarget)
} else {
    Join-Path $env:LOCALAPPDATA 'OpenRevit Tools\Bridge'
}

New-Item -ItemType Directory -Force -Path $addinRoot, $pluginTarget, $bridgeTargetPath | Out-Null
$pluginFiles = @('JarviTools.dll', 'Newtonsoft.Json.dll')
foreach ($file in $pluginFiles) {
    $source = Join-Path $pluginOutput $file
    if (!(Test-Path -LiteralPath $source)) { throw "Required plugin file not found: $source" }
    Copy-Item -LiteralPath $source -Destination $pluginTarget -Force
}
$pluginPdb = Join-Path $pluginOutput 'JarviTools.pdb'
if ($IncludeSymbols -and (Test-Path -LiteralPath $pluginPdb)) {
    Copy-Item -LiteralPath $pluginPdb -Destination $pluginTarget -Force
} elseif (Test-Path -LiteralPath (Join-Path $pluginTarget 'JarviTools.pdb')) {
    Remove-Item -LiteralPath (Join-Path $pluginTarget 'JarviTools.pdb') -Force
}
$resources = if ($isReleasePackage) { Join-Path $repoRoot 'Resources' } else { Join-Path $pluginOutput 'Resources' }
if (Test-Path -LiteralPath $resources) { Copy-Item -LiteralPath $resources -Destination $pluginTarget -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $pluginTarget -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $pluginTarget -Force
$licenses = Join-Path $repoRoot 'licenses'
if (Test-Path -LiteralPath $licenses) { Copy-Item -LiteralPath $licenses -Destination $pluginTarget -Recurse -Force }

$bridgeFiles = @('RevitMcpBridge.exe', 'RevitMcpBridge.exe.config', 'Newtonsoft.Json.dll')
foreach ($file in $bridgeFiles) {
    $source = Join-Path $bridgeOutput $file
    if (!(Test-Path -LiteralPath $source)) { throw "Required bridge file not found: $source" }
    Copy-Item -LiteralPath $source -Destination $bridgeTargetPath -Force
}
if (!(Test-Path -LiteralPath (Join-Path $bridgeTargetPath 'RevitMcpBridge.exe'))) {
    throw 'Bridge executable was not installed.'
}

$template = Get-Content -LiteralPath (Join-Path $repoRoot 'JarviTools.addin.template') -Raw -Encoding UTF8
$escapedAssembly = [System.Security.SecurityElement]::Escape((Join-Path $pluginTarget 'JarviTools.dll'))
$manifest = $template.Replace('__ASSEMBLY_PATH__', $escapedAssembly)
if ($manifest.Contains('__ASSEMBLY_PATH__')) { throw 'Manifest template placeholder was not replaced.' }
try { [xml]$manifest | Out-Null } catch { throw "Generated Revit manifest is invalid XML: $($_.Exception.Message)" }
[System.IO.File]::WriteAllText($manifestTarget, $manifest, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Installed OpenRevit Tools: $manifestTarget"
Write-Host "MCP bridge: $bridgeTargetPath\RevitMcpBridge.exe"
Write-Host 'Restart Revit 2024, then use OpenRevit > Start MCP Server.'
