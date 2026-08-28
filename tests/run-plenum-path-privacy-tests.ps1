[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$modelsPath = Join-Path $repoRoot 'src\Commands\Plenum\PlenumModels.cs'
$toolsPath = Join-Path $repoRoot 'src\Mcp\Tools\PlenumAnalysisTools.cs'
$legacyMigrationPath = Join-Path $repoRoot 'src\Commands\MaintenanceReachability\MaintenanceHandReachLegacyMigrationService.cs'
$models = Get-Content -LiteralPath $modelsPath -Raw -Encoding UTF8
$tools = Get-Content -LiteralPath $toolsPath -Raw -Encoding UTF8
$legacyMigration = Get-Content -LiteralPath $legacyMigrationPath -Raw -Encoding UTF8

function Assert-Match {
    param([string]$Text, [string]$Pattern, [string]$Message)
    if ($Text -notmatch $Pattern) { throw $Message }
}

function Assert-NoMatch {
    param([string]$Text, [string]$Pattern, [string]$Message)
    if ($Text -match $Pattern) { throw $Message }
}

Assert-Match $models 'ToSummaryJson\(bool includePath = false\)' `
    'Plenum summary must hide paths by default.'
Assert-Match $models '\["documentPath"\] = includePath && !string\.IsNullOrWhiteSpace\(DocumentPath\)' `
    'Plenum summary path must be conditional on includePath.'
Assert-Match $models '\["pathIncluded"\] = includePath' `
    'Plenum summary must declare whether a path was included.'
Assert-NoMatch $models '\["documentPath"\]\s*=\s*DocumentPath\s*,' `
    'An unconditional documentPath assignment remains in the summary.'

Assert-Match $tools '\["includePath"\] = new JObject' `
    'The MCP schema must document includePath.'
Assert-Match $tools 'InputSchema => AnalysisIdSchema\(true\)' `
    'get_plenum_analysis_summary must expose includePath in its schema.'
Assert-Match $tools 'ToSummaryJson\(ReadBool\(input, "includePath", false\)\)' `
    'analyze_plenum_space must pass its explicit includePath choice.'
Assert-Match $tools 'ToSummaryJson\(includePath\)' `
    'get_plenum_analysis_summary must pass its explicit includePath choice.'
Assert-NoMatch $tools 'return result\.ToSummaryJson\(\);' `
    'A Plenum MCP summary call still bypasses the privacy flag.'

Assert-Match $legacyMigration 'Environment\.SpecialFolder\.LocalApplicationData' `
    'Legacy archives must default to the current user local application data directory.'
Assert-NoMatch $legacyMigration '[A-Za-z]:\\' `
    'Legacy archive code still contains a development-machine absolute path.'

Write-Host 'Path privacy contract: 11/11 assertions passed.'
