[CmdletBinding()]
param([switch]$RemoveLocalMcpState)

$ErrorActionPreference = 'Stop'
$addinRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024'
$pluginTarget = Join-Path $addinRoot 'OpenRevitTools'
$manifestTarget = Join-Path $addinRoot 'OpenRevitTools.addin'
$bridgeTarget = Join-Path $env:LOCALAPPDATA 'OpenRevit Tools\Bridge'

if (Test-Path -LiteralPath $manifestTarget) { Remove-Item -LiteralPath $manifestTarget -Force }
if ((Split-Path -Leaf $pluginTarget) -eq 'OpenRevitTools' -and (Test-Path -LiteralPath $pluginTarget)) {
    Remove-Item -LiteralPath $pluginTarget -Recurse -Force
}
if ((Split-Path -Leaf $bridgeTarget) -eq 'Bridge' -and (Test-Path -LiteralPath $bridgeTarget)) {
    Remove-Item -LiteralPath $bridgeTarget -Recurse -Force
}
if ($RemoveLocalMcpState) {
    $stateRoot = Join-Path $env:LOCALAPPDATA 'OpenRevit Tools\Mcp'
    if ((Split-Path -Leaf $stateRoot) -eq 'Mcp' -and (Test-Path -LiteralPath $stateRoot)) {
        Remove-Item -LiteralPath $stateRoot -Recurse -Force
    }
}

Write-Host 'OpenRevit Tools was uninstalled. Project models and generated model content were not deleted.'
