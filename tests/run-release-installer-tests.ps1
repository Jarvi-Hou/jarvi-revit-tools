[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = Split-Path -Parent $PSScriptRoot
$systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$testRoot = Join-Path $systemTemp ("OpenRevitTools-installer-test-{0}" -f [Guid]::NewGuid().ToString('N'))
$resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
if (!$resolvedTestRoot.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create test fixture outside the system temp directory: $resolvedTestRoot"
}

try {
    $packageRoot = Join-Path $resolvedTestRoot 'Package'
    $packageScripts = Join-Path $packageRoot 'scripts'
    $pluginSource = Join-Path $packageRoot 'Plugin'
    $bridgeSource = Join-Path $packageRoot 'Bridge'
    $resourcesSource = Join-Path $packageRoot 'Resources\icons'
    $licensesSource = Join-Path $packageRoot 'licenses'
    New-Item -ItemType Directory -Force -Path $packageScripts, $pluginSource, $bridgeSource, $resourcesSource, $licensesSource | Out-Null

    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\install.ps1') -Destination $packageScripts
    Copy-Item -LiteralPath (Join-Path $repoRoot 'JarviTools.addin.template') -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $packageRoot

    Set-Content -LiteralPath (Join-Path $pluginSource 'JarviTools.dll') -Value 'plugin-fixture' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $pluginSource 'Newtonsoft.Json.dll') -Value 'json-fixture' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $bridgeSource 'RevitMcpBridge.exe') -Value 'bridge-fixture' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $bridgeSource 'RevitMcpBridge.exe.config') -Value '<configuration />' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $bridgeSource 'Newtonsoft.Json.dll') -Value 'bridge-json-fixture' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $resourcesSource 'fixture.png') -Value 'resource-fixture' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $licensesSource 'fixture.txt') -Value 'license-fixture' -Encoding ASCII

    $addinRoot = Join-Path $resolvedTestRoot 'Addins\2024'
    $pluginTarget = Join-Path $addinRoot 'OpenRevitTools'
    $bridgeTarget = Join-Path $resolvedTestRoot 'LocalAppData\OpenRevit Tools\Bridge'
    New-Item -ItemType Directory -Force -Path $pluginTarget | Out-Null
    Set-Content -LiteralPath (Join-Path $pluginTarget 'JarviTools.pdb') -Value 'stale-symbol' -Encoding ASCII

    & (Join-Path $packageScripts 'install.ps1') -AddinRoot $addinRoot -BridgeTarget $bridgeTarget

    $manifestPath = Join-Path $addinRoot 'OpenRevitTools.addin'
    $expectedFiles = @(
        (Join-Path $pluginTarget 'JarviTools.dll'),
        (Join-Path $pluginTarget 'Newtonsoft.Json.dll'),
        (Join-Path $pluginTarget 'Resources\icons\fixture.png'),
        (Join-Path $pluginTarget 'LICENSE'),
        (Join-Path $pluginTarget 'THIRD_PARTY_NOTICES.md'),
        (Join-Path $pluginTarget 'licenses\fixture.txt'),
        (Join-Path $bridgeTarget 'RevitMcpBridge.exe'),
        (Join-Path $bridgeTarget 'RevitMcpBridge.exe.config'),
        (Join-Path $bridgeTarget 'Newtonsoft.Json.dll'),
        $manifestPath
    )
    foreach ($expectedFile in $expectedFiles) {
        if (!(Test-Path -LiteralPath $expectedFile -PathType Leaf)) {
            throw "Expected installed file is missing: $expectedFile"
        }
    }
    if (Test-Path -LiteralPath (Join-Path $pluginTarget 'JarviTools.pdb')) {
        throw 'The release installer retained a stale PDB in the plugin target.'
    }

    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
    $assemblyPath = [string]$manifest.RevitAddIns.AddIn.Assembly
    $expectedAssemblyPath = Join-Path $pluginTarget 'JarviTools.dll'
    if (![string]::Equals($assemblyPath, $expectedAssemblyPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Generated manifest points to '$assemblyPath' instead of '$expectedAssemblyPath'."
    }
    if ((Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8).Contains('__ASSEMBLY_PATH__')) {
        throw 'Generated manifest still contains the assembly placeholder.'
    }

    Write-Host 'Release installer tests: 10/10 passed.'
}
finally {
    if ((Test-Path -LiteralPath $resolvedTestRoot) -and
        $resolvedTestRoot.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
