$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $repoRoot 'obj\MaintenanceLedgerTests'
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null

$windowsDirectory = if ($env:WINDIR) { $env:WINDIR } else { 'C:\Windows' }
$compiler = Join-Path $windowsDirectory 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw "C# compiler not found: $compiler"
}

$output = Join-Path $buildDirectory 'MaintenanceLedgerCsvTests.exe'
& $compiler /nologo /target:exe /out:$output `
    (Join-Path $repoRoot 'src\Commands\MaintenanceReachability\MaintenanceLedgerCsv.cs') `
    (Join-Path $repoRoot 'tests\MaintenanceLedgerCsvTests.cs')
if ($LASTEXITCODE -ne 0) { throw "Ledger CSV test compilation failed: $LASTEXITCODE" }

& $output $buildDirectory
if ($LASTEXITCODE -ne 0) { throw "Ledger CSV tests failed: $LASTEXITCODE" }
