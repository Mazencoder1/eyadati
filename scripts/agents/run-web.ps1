# Runs the Next.js frontend for THIS worktree on its own port, pointed at this worktree's API.
# Usage (from the worktree root):  powershell -File scripts/agents/run-web.ps1
. "$PSScriptRoot/agent-env.ps1"
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'Node.js/npm not found on PATH.' }

Push-Location "$AgentRoot/frontend"
try {
    if (-not (Test-Path node_modules)) { npm ci; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
    npx next dev -p $AgentWebPort
    exit $LASTEXITCODE
} finally { Pop-Location }
