[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$serverPath = Join-Path $repoRoot 'src\Mcp\Server\HttpServer.cs'
$server = Get-Content -LiteralPath $serverPath -Raw -Encoding UTF8
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

Assert-NoMatch $server 'Headers\s*\[\s*"WWW-Authenticate"\s*\]' `
    'HttpListener must not set the restricted WWW-Authenticate header through Headers.'
Assert-Match $server 'if\s*\(!IsAuthorized\(req\)\)[\s\S]*?WriteJson\(resp,\s*403,' `
    'Invalid bearer credentials must return a clean 403 response.'
Assert-Match $server 'forbidden:\s*use the bundled OpenRevit MCP bridge' `
    'The authentication error must tell clients to use the bundled bridge.'

Write-Host ("HTTP authentication response contract: {0}/{0} assertions passed." -f $assertions)
