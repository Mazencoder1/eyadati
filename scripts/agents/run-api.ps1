# Runs the API for THIS worktree on its own port and its own LocalDB dev database.
# Usage (from the worktree root):  powershell -File scripts/agents/run-api.ps1
. "$PSScriptRoot/agent-env.ps1"
if (-not $DotnetExe) { throw '.NET SDK not found (expected dotnet on PATH or C:\Program Files\dotnet).' }

# --no-launch-profile so launchSettings.json (fixed port 5056) does not override ASPNETCORE_URLS.
& $DotnetExe run --project "$AgentRoot/backend/src/ClinicSaaS.Api" --no-launch-profile
exit $LASTEXITCODE
