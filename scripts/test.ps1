[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot 'tests\MaintenancePathfinder.Tests\MaintenancePathfinder.Tests.csproj'
$handReachTestProject = Join-Path $repoRoot 'tests\MaintenanceHandReach.Tests\MaintenanceHandReach.Tests.csproj'
$plenumTestProject = Join-Path $repoRoot 'tests\PlenumRegionMerger.Tests\PlenumRegionMerger.Tests.csproj'
$paginationTestProject = Join-Path $repoRoot 'tests\PaginationOptions.Tests\PaginationOptions.Tests.csproj'
$mcpResourceTestProject = Join-Path $repoRoot 'tests\McpResourceLimits.Tests\McpResourceLimits.Tests.csproj'
$plenumPathPrivacyTest = Join-Path $repoRoot 'tests\run-plenum-path-privacy-tests.ps1'
$equipmentSectionSafetyTest = Join-Path $repoRoot 'tests\run-equipment-section-safety-tests.ps1'
$httpAuthContractTest = Join-Path $repoRoot 'tests\run-http-auth-contract-tests.ps1'
$maintenanceLedgerTest = Join-Path $repoRoot 'tests\run-maintenance-ledger-tests.ps1'
$ribbonSurfaceTest = Join-Path $repoRoot 'tests\run-ribbon-surface-tests.ps1'

& dotnet run --project $testProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }

& dotnet run --project $handReachTestProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "HandReach tests failed with exit code $LASTEXITCODE." }

& dotnet run --project $plenumTestProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Plenum region tests failed with exit code $LASTEXITCODE." }

& dotnet run --project $paginationTestProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Pagination tests failed with exit code $LASTEXITCODE." }

& dotnet run --project $mcpResourceTestProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "MCP resource-limit tests failed with exit code $LASTEXITCODE." }

& (Join-Path $repoRoot 'tests\run-release-installer-tests.ps1')
if ($LASTEXITCODE -ne 0) { throw "Release installer tests failed with exit code $LASTEXITCODE." }

& $plenumPathPrivacyTest

& $equipmentSectionSafetyTest

& $httpAuthContractTest

& $maintenanceLedgerTest

& $ribbonSurfaceTest
