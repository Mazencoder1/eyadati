# Per-worktree development environment for parallel agents.
# Dot-source it:  . scripts/agents/agent-env.ps1
# Gives every agent its own API port, web port and LocalDB dev database, so parallel
# agents never share (or auto-migrate) the same dev database or collide on ports.
# Integration tests are already isolated: ApiFactory creates ClinicSaaS_Test_<guid> per run.

$ErrorActionPreference = 'Stop'

$AgentPorts = @{
    'integration'       = @{ Api = 5100; Web = 3100 }
    'agent/backend'     = @{ Api = 5110; Web = 3110 }
    'agent/frontend'    = @{ Api = 5120; Web = 3120 }
    'agent/qa-security' = @{ Api = 5130; Web = 3130 }
    'agent/database'    = @{ Api = 5140; Web = 3140 }
}

$AgentRoot   = (git rev-parse --show-toplevel).Trim()
$AgentBranch = (git symbolic-ref --short -q HEAD).Trim()

if ($AgentBranch -eq 'main') {
    throw "This is the main checkout. Agents never run here - use your worktree under C:\Users\Yazee\clinic-worktrees\."
}
if (-not $AgentPorts.ContainsKey($AgentBranch)) {
    throw "Branch '$AgentBranch' has no port allocation in scripts/agents/agent-env.ps1."
}

$AgentSlug   = ($AgentBranch -replace '^agent/', '') -replace '[^A-Za-z0-9]', '_'
$AgentApiUrl = "http://localhost:$($AgentPorts[$AgentBranch].Api)"
$AgentWebUrl = "http://localhost:$($AgentPorts[$AgentBranch].Web)"
$AgentWebPort = $AgentPorts[$AgentBranch].Web
$AgentDbName = "ClinicSaaS_Dev_$AgentSlug"

# LocalDB only. Never point an agent at a shared, staging or production server.
$env:ASPNETCORE_ENVIRONMENT      = 'Development'
$env:ASPNETCORE_URLS             = $AgentApiUrl
$env:ConnectionStrings__Default  = "Server=(localdb)\MSSQLLocalDB;Database=$AgentDbName;Trusted_Connection=True;TrustServerCertificate=True"
$env:Cors__AllowedOrigins__0     = $AgentWebUrl
$env:NEXT_PUBLIC_API_URL         = $AgentApiUrl

$DotnetExe = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $DotnetExe -and (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) { $DotnetExe = 'C:\Program Files\dotnet\dotnet.exe' }

Write-Host "[agent-env] branch=$AgentBranch api=$AgentApiUrl web=$AgentWebUrl db=$AgentDbName"
