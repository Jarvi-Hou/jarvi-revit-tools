[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'PlenumRegionMerger.Tests\PlenumRegionMerger.Tests.csproj'
& dotnet run --project $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Plenum region tests failed with exit code $LASTEXITCODE." }
