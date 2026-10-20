# Verification gate. Every agent runs it before asking for a merge; the Manager runs it
# on integration after every merge; promote-to-main.ps1 runs it before touching main.
#
# Usage:  powershell -File scripts/agents/verify.ps1 [-Scope all|backend|frontend]
# Exit code 0 = everything passed. Any failure stops immediately with a non-zero exit code.
param([ValidateSet('all', 'backend', 'frontend')][string]$Scope = 'all')

$ErrorActionPreference = 'Stop'
$root = (git rev-parse --show-toplevel).Trim()
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
# dotnet-ef starts "dotnet" by name and global tools live in ~/.dotnet/tools, so both must be on PATH (resolving
# $dotnet alone made the EF check fail or be skipped). Node.js installed by MSI may not be on an older session's PATH.
if ($dotnet) { $env:Path = "$(Split-Path $dotnet);$env:USERPROFILE\.dotnet\tools;$env:Path" }
if (-not (Get-Command npm -ErrorAction SilentlyContinue) -and (Test-Path 'C:\Program Files\nodejs\npm.cmd')) { $env:Path = "C:\Program Files\nodejs;$env:Path" }

function Step([string]$name, [scriptblock]$body) {
    Write-Host "`n==> $name" -ForegroundColor Cyan
    & $body
    if ($LASTEXITCODE -ne 0) { Write-Host "FAILED: $name (exit $LASTEXITCODE)" -ForegroundColor Red; exit $LASTEXITCODE }
}

$conflicts = git -C $root diff --name-only --diff-filter=U
if ($conflicts) { Write-Host "Unresolved merge conflicts:`n$conflicts" -ForegroundColor Red; exit 1 }
$markers = git -C $root grep -nE '^(<<<<<<<|>>>>>>>) ' -- . ':!*.md' 2>$null
if ($markers) { Write-Host "Conflict markers left in files:`n$markers" -ForegroundColor Red; exit 1 }

if ($Scope -in 'all', 'backend') {
    if (-not $dotnet) { Write-Host '.NET SDK not found.' -ForegroundColor Red; exit 1 }
    $sln = "$root/backend/ClinicSaaS.slnx"
    Step 'backend: build'  { & $dotnet build $sln -nologo }
    Step 'backend: tests (unit + integration on throw-away LocalDB databases)' { & $dotnet test $sln --no-build -nologo }

    # Catches entity/configuration changes that were committed without a migration.
    # PowerShell 5.1 turns native stderr into a terminating error under 'Stop'; probe the tool without that.
    $efInstalled = $false
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $dotnet ef --version *> $null; $efInstalled = ($LASTEXITCODE -eq 0) } catch { $efInstalled = $false }
    $ErrorActionPreference = $prev
    if ($efInstalled) {
        Step 'backend: EF model has no pending changes without a migration' {
            & $dotnet ef migrations has-pending-model-changes -p "$root/backend/src/ClinicSaaS.Infrastructure" -s "$root/backend/src/ClinicSaaS.Api" --no-build
        }
    } else {
        Write-Host 'WARNING: dotnet-ef not installed; skipped pending-model-changes check (dotnet tool install -g dotnet-ef).' -ForegroundColor Yellow
        $global:LASTEXITCODE = 0
    }
}

if ($Scope -in 'all', 'frontend') {
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { Write-Host 'Node.js/npm not found on PATH.' -ForegroundColor Red; exit 1 }
    Push-Location "$root/frontend"
    try {
        if (-not (Test-Path node_modules)) { Step 'frontend: npm ci' { npm ci } }
        Step 'frontend: lint'      { npm run lint }
        Step 'frontend: typecheck' { npm run typecheck }
        Step 'frontend: tests'     { npm test }
        Step 'frontend: build'     { npm run build }
    } finally { Pop-Location }
}

Write-Host "`nALL CHECKS PASSED ($Scope) at $(git -C $root rev-parse --short HEAD)" -ForegroundColor Green
exit 0
