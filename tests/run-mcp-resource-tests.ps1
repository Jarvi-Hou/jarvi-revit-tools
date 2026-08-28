[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$project = Join-Path $PSScriptRoot 'McpResourceLimits.Tests\McpResourceLimits.Tests.csproj'
& dotnet run --project $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "MCP resource-limit tests failed with exit code $LASTEXITCODE." }
