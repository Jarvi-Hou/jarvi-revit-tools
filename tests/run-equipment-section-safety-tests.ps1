[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$originalCommandPath = Join-Path $repoRoot 'src\Commands\EquipmentSection\EquipmentSectionCommand.cs'
$safeCommandPath = Join-Path $repoRoot 'src\Commands\EquipmentSection\SafeEquipmentSectionCommand.cs'
$transactionSafetyPath = Join-Path $repoRoot 'src\Core\TransactionSafety.cs'
$applicationPath = Join-Path $repoRoot 'src\Application.cs'
$projectPath = Join-Path $repoRoot 'JarviTools.csproj'

$originalCommand = Get-Content -LiteralPath $originalCommandPath -Raw -Encoding UTF8
$transactionSafety = Get-Content -LiteralPath $transactionSafetyPath -Raw -Encoding UTF8
$application = Get-Content -LiteralPath $applicationPath -Raw -Encoding UTF8
$project = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
$assertions = 0

function Assert-Match {
    param([string]$Text, [string]$Pattern, [string]$Message)
    if ($Text -notmatch $Pattern) { throw $Message }
    $script:assertions++
}

function Assert-NoMatch {
    param([string]$Text, [string]$Pattern, [string]$Message)
    if ($Text -match $Pattern) { throw $Message }
    $script:assertions++
}

Assert-Match $application 'JarviTools\.Commands\.EquipmentSection\.EquipmentSectionCommand' `
    'The equipment-section ribbon button is not bound to the original safe command.'
Assert-NoMatch $application 'SafeEquipmentSectionCommand' `
    'The ribbon still references the temporary duplicate command.'
Assert-NoMatch $project 'SafeEquipmentSectionCommand\.cs' `
    'The project still compiles the temporary duplicate command.'
if (Test-Path -LiteralPath $safeCommandPath) {
    throw 'The temporary duplicate command still exists.'
}
$assertions++

Assert-NoMatch $originalCommand '\b(?:group|tx)\.(?:Start|Commit|Assimilate|RollBack)\s*\(' `
    'The original equipment-section command contains a direct transaction state-changing call.'
Assert-Match $originalCommand 'TransactionSafety\.Start\(\s*group,' `
    'The original command does not validate the transaction-group start result.'
Assert-Match $originalCommand 'TransactionSafety\.Start\(\s*tx,' `
    'The original command does not validate the per-equipment transaction start result.'
Assert-Match $originalCommand 'TransactionSafety\.Commit\(\s*tx,' `
    'The original command does not validate the per-equipment commit result.'
Assert-Match $originalCommand 'TransactionSafety\.Assimilate\(\s*group,' `
    'The original command does not validate the transaction-group assimilate result.'
Assert-Match $originalCommand 'TransactionSafety\.RollBack\(\s*tx,' `
    'The original command does not roll back a failed per-equipment transaction.'
Assert-Match $originalCommand 'TransactionSafety\.RollBack\(\s*group,' `
    'The original command does not roll back a fatal batch failure.'
Assert-Match $originalCommand 'TransactionSafety\.Commit\([\s\S]*?\}\s*\}\s*created\.Add\(createdViewName\)' `
    'The original command may count a view as successful before its transaction commits.'
Assert-Match $originalCommand 'TransactionSafety\.Assimilate\([\s\S]*?groupCompleted\s*=\s*true' `
    'The original command marks the batch complete before its transaction group assimilates.'

Assert-Match $transactionSafety 'void Start\(Transaction transaction,' `
    'TransactionSafety does not validate Transaction.Start.'
Assert-Match $transactionSafety 'void Start\(TransactionGroup transactionGroup,' `
    'TransactionSafety does not validate TransactionGroup.Start.'
Assert-Match $transactionSafety 'void RollBack\(Transaction transaction,' `
    'TransactionSafety does not validate Transaction.RollBack.'
Assert-Match $transactionSafety 'void RollBack\(TransactionGroup transactionGroup,' `
    'TransactionSafety does not validate TransactionGroup.RollBack.'
Assert-Match $transactionSafety 'TransactionStatus\.Started' `
    'TransactionSafety does not require the Started state.'
Assert-Match $transactionSafety 'TransactionStatus\.RolledBack' `
    'TransactionSafety does not require the RolledBack state.'

Write-Host ("Equipment-section transaction safety contract: {0}/{0} assertions passed." -f $assertions)
