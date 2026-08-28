[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$PluginOutputDirectory,
    [string]$BridgeOutputDirectory,
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = Split-Path -Parent $PSScriptRoot
if ($Build) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }

if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot (
        'artifacts\OpenRevit-Tools-{0}-{1}' -f $Configuration, (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$staging = [System.IO.Path]::GetFullPath($OutputDirectory)
$zipPath = $staging.TrimEnd('\') + '.zip'

if (Test-Path -LiteralPath $staging) { throw "Release staging already exists: $staging" }
if (Test-Path -LiteralPath $zipPath) { throw "Release archive already exists: $zipPath" }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$pluginOutput = if ($PluginOutputDirectory) {
    [System.IO.Path]::GetFullPath($PluginOutputDirectory)
} else {
    Join-Path $repoRoot ("bin\{0}" -f $Configuration)
}
$bridgeOutput = if ($BridgeOutputDirectory) {
    [System.IO.Path]::GetFullPath($BridgeOutputDirectory)
} else {
    Join-Path $repoRoot ("bridge\RevitMcpBridge\bin\{0}\net48" -f $Configuration)
}
$required = @(
    @{ Source = (Join-Path $pluginOutput 'JarviTools.dll'); Target = 'Plugin\JarviTools.dll' },
    @{ Source = (Join-Path $pluginOutput 'Newtonsoft.Json.dll'); Target = 'Plugin\Newtonsoft.Json.dll' },
    @{ Source = (Join-Path $bridgeOutput 'RevitMcpBridge.exe'); Target = 'Bridge\RevitMcpBridge.exe' },
    @{ Source = (Join-Path $bridgeOutput 'RevitMcpBridge.exe.config'); Target = 'Bridge\RevitMcpBridge.exe.config' },
    @{ Source = (Join-Path $bridgeOutput 'Newtonsoft.Json.dll'); Target = 'Bridge\Newtonsoft.Json.dll' },
    @{ Source = (Join-Path $repoRoot 'JarviTools.addin.template'); Target = 'JarviTools.addin.template' },
    @{ Source = (Join-Path $repoRoot 'README.md'); Target = 'README.md' },
    @{ Source = (Join-Path $repoRoot 'LICENSE'); Target = 'LICENSE' },
    @{ Source = (Join-Path $repoRoot 'SECURITY.md'); Target = 'SECURITY.md' },
    @{ Source = (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md'); Target = 'THIRD_PARTY_NOTICES.md' },
    @{ Source = (Join-Path $repoRoot 'scripts\install.ps1'); Target = 'scripts\install.ps1' },
    @{ Source = (Join-Path $repoRoot 'scripts\uninstall.ps1'); Target = 'scripts\uninstall.ps1' }
)
foreach ($item in $required) {
    if (!(Test-Path -LiteralPath $item.Source)) { throw "Required release input missing: $($item.Source)" }
    $destination = Join-Path $staging $item.Target
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $item.Source -Destination $destination -Force
}

foreach ($directory in @('Resources', 'licenses')) {
    $source = if ($directory -eq 'Resources') { Join-Path $pluginOutput $directory } else { Join-Path $repoRoot $directory }
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $staging $directory) -Recurse -Force
    }
}

$forbiddenReleaseExtensions = @('.pdb', '.rvt', '.rfa', '.rte', '.rft', '.dwg', '.dxf', '.ifc', '.nwc', '.nwd')
$forbiddenReleaseFiles = Get-ChildItem -LiteralPath $staging -File -Recurse | Where-Object {
    $forbiddenReleaseExtensions -contains $_.Extension.ToLowerInvariant()
}
if ($forbiddenReleaseFiles) {
    $relativeNames = $forbiddenReleaseFiles | ForEach-Object {
        $_.FullName.Substring($staging.Length).TrimStart('\')
    }
    throw "Forbidden debug/customer file found in release staging: $($relativeNames -join ', ')"
}

$manifest = Get-ChildItem -LiteralPath $staging -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        [PSCustomObject]@{
            path = $_.FullName.Substring($staging.Length).TrimStart('\').Replace('\', '/')
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'manifest.sha256.json') -Encoding UTF8

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Release package: $zipPath"
Write-Host 'The package is whitelist-built and cannot include ignored customer BIM/CAD files or local PDB paths.'
