[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$applicationPath = Join-Path $repoRoot 'src\Application.cs'
$readmePath = Join-Path $repoRoot 'README.md'
$application = Get-Content -LiteralPath $applicationPath -Raw
$readme = Get-Content -LiteralPath $readmePath -Raw
$assertions = 0

function Assert-Contains([string]$text, [string]$pattern, [string]$message) {
    if ($text -notmatch $pattern) { throw $message }
    $script:assertions++
}

foreach ($method in @('CreateExportPanel', 'CreateSchedulePanel', 'CreateParameterPanel', 'CreateMepCheckPanel')) {
    Assert-Contains $application ([regex]::Escape($method + '(application, tabName);')) "Experimental panel registration is missing: $method"
}

$hiddenPanelCount = [regex]::Matches($application, 'panel\.Visible\s*=\s*false;').Count
if ($hiddenPanelCount -ne 4) {
    throw "Expected exactly four hidden experimental panels; found $hiddenPanelCount."
}
$assertions++

foreach ($commandId in @(
    'MatchQuantityParameters',
    'FilterUnmatchedElements',
    'ExportVisibleElements',
    'ExportAllSchedules',
    'ParameterManager',
    'EquipmentSection',
    'Equipment3DView',
    'ClearanceAnalysis',
    'PlenumSpaceField',
    'MaintenanceReachability'
)) {
    Assert-Contains $application ('PushButtonData\("' + [regex]::Escape($commandId) + '"') "Experimental command is no longer registered: $commandId"
}

foreach ($commandId in @('MCP_Start', 'MCP_Stop', 'MCP_Status')) {
    Assert-Contains $application ('PushButtonData\("' + [regex]::Escape($commandId) + '"') "Stable MCP command is missing: $commandId"
}

Assert-Contains $readme '启动 MCP' 'README does not document the stable Start MCP command.'
Assert-Contains $readme '停止 MCP' 'README does not document the stable Stop MCP command.'
Assert-Contains $readme '状态 \+ 工具' 'README does not document the stable Status + Tools command.'
Assert-Contains $readme '默认不显示在稳定功能区' 'README does not disclose the experimental Ribbon boundary.'

Write-Host "Ribbon surface contract: $assertions/$assertions assertions passed."
