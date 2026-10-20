# Local TRIAL run of Clinic SaaS (Development mode, fake data only — never real patients).
#
#   .\start-trial.ps1            start the API (port 5056) and the web app (port 3000), open the browser
#   .\start-trial.ps1 -Rebuild   rebuild the web app first (after pulling new code)
#   .\start-trial.ps1 -Stop      stop both
#   .\start-trial.ps1 -Status    show what is running
#
# Uses its own database (ClinicSaaS_Trial on LocalDB), never ClinicSaaS_Dev. The local platform-admin password is
# generated once and kept OUTSIDE the repository in %USERPROFILE%\.clinic-saas-trial\credentials.txt.

param([switch]$Rebuild, [switch]$Stop, [switch]$Status)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$home_ = Join-Path $env:USERPROFILE ".clinic-saas-trial"
$logs = Join-Path $home_ "logs"
$credFile = Join-Path $home_ "credentials.txt"
$database = "ClinicSaaS_Trial"
$apiPort = 5056
$webPort = 3000
$mailbox = Join-Path $root "backend\src\ClinicSaaS.Api\.dev-mailbox"

function Listening($port) {
    Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique
}

function StopPort($port, $name) {
    $pids = Listening $port
    if (-not $pids) { Write-Host "  $name is not running"; return }
    foreach ($p in $pids) { Stop-Process -Id $p -Force -ErrorAction SilentlyContinue }
    Write-Host "  $name stopped (port $port)"
}

if ($Stop) {
    Write-Host "Stopping Clinic SaaS trial..."
    StopPort $webPort "Web app"
    StopPort $apiPort "API"
    exit 0
}

if ($Status) {
    Write-Host ("API     (port {0}): {1}" -f $apiPort, $(if (Listening $apiPort) { "running" } else { "stopped" }))
    Write-Host ("Web app (port {0}): {1}" -f $webPort, $(if (Listening $webPort) { "running" } else { "stopped" }))
    exit 0
}

# ---------------------------------------------------------------- checks
foreach ($tool in "dotnet", "node", "npm", "sqllocaldb") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool was not found. Install it first." }
}
if ((Listening $apiPort) -or (Listening $webPort)) {
    Write-Host "Something is already using port $apiPort or $webPort. If it is a previous trial run: .\start-trial.ps1 -Stop" -ForegroundColor Yellow
    exit 1
}
& sqllocaldb start MSSQLLocalDB | Out-Null
New-Item -ItemType Directory -Force -Path $logs | Out-Null

# ---------------------------------------------------------------- local admin credentials (outside the repo)
if (-not (Test-Path $credFile)) {
    $chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789"
    $bytes = New-Object byte[] 20
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $random = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    @("AdminEmail=admin@trial.local", "AdminPassword=Trial-$random-9a") | Set-Content -Path $credFile -Encoding UTF8
}
$cred = @{}
Get-Content $credFile | ForEach-Object { $k, $v = $_ -split "=", 2; $cred[$k] = $v }

# ---------------------------------------------------------------- web app build
$frontend = Join-Path $root "frontend"
if (-not (Test-Path (Join-Path $frontend "node_modules"))) {
    Write-Host "Installing web app packages (first run only)..."
    Push-Location $frontend; try { & npm ci; if ($LASTEXITCODE) { throw "npm ci failed" } } finally { Pop-Location }
}
if ($Rebuild -or -not (Test-Path (Join-Path $frontend ".next\BUILD_ID"))) {
    Write-Host "Building the web app (takes a minute)..."
    Push-Location $frontend; try { & npm run build; if ($LASTEXITCODE) { throw "web build failed" } } finally { Pop-Location }
}

# ---------------------------------------------------------------- start (child processes inherit these variables)
$env:ConnectionStrings__Default = "Server=(localdb)\MSSQLLocalDB;Database=$database;Trusted_Connection=True;TrustServerCertificate=True"
$env:Bootstrap__SuperAdminEmail = $cred["AdminEmail"]
$env:Bootstrap__SuperAdminPassword = $cred["AdminPassword"]

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$apiLog = Join-Path $logs "api-$stamp.log"
$webLog = Join-Path $logs "web-$stamp.log"

Write-Host "Starting the API (first start creates the trial database)..."
Start-Process -FilePath "dotnet" -WorkingDirectory (Join-Path $root "backend") -WindowStyle Hidden `
    -ArgumentList "run", "--project", "src/ClinicSaaS.Api", "--launch-profile", "http" `
    -RedirectStandardOutput $apiLog -RedirectStandardError "$apiLog.err" | Out-Null

Write-Host "Starting the web app..."
Start-Process -FilePath "cmd.exe" -WorkingDirectory $frontend -WindowStyle Hidden `
    -ArgumentList "/c", "npm run start" -RedirectStandardOutput $webLog -RedirectStandardError "$webLog.err" | Out-Null

# Clear the bootstrap secrets from this shell; the API already has them.
Remove-Item Env:Bootstrap__SuperAdminPassword, Env:Bootstrap__SuperAdminEmail -ErrorAction SilentlyContinue

$ready = $false
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep -Seconds 2
    try {
        $api = (Invoke-WebRequest -UseBasicParsing "http://localhost:$apiPort/health/ready" -TimeoutSec 3).StatusCode -eq 200
        $web = (Invoke-WebRequest -UseBasicParsing "http://localhost:$webPort/login" -TimeoutSec 3).StatusCode -eq 200
        if ($api -and $web) { $ready = $true; break }
    } catch { }
}
if (-not $ready) {
    Write-Host "The system did not become ready within 3 minutes. Logs: $logs" -ForegroundColor Red
    exit 1
}

Start-Process "http://localhost:$webPort"
Write-Host ""
Write-Host "Clinic SaaS trial is running (fake data only)" -ForegroundColor Green
Write-Host "  Web app : http://localhost:$webPort"
Write-Host "  API     : http://localhost:$apiPort   (health: /health/ready)"
Write-Host "  Database: $database (LocalDB)"
Write-Host ""
Write-Host "  Clinic owner : register a new clinic at http://localhost:$webPort/register"
Write-Host ("  Platform admin: {0} / {1}" -f $cred["AdminEmail"], $cred["AdminPassword"])
Write-Host "                 (asks you to set up an authenticator app on first sign-in; keep the recovery codes)"
Write-Host ""
Write-Host "  Emails (invitations, password reset, verification) are NOT sent; they are saved as files in:"
Write-Host "    $mailbox"
Write-Host "  Logs: $logs"
Write-Host "  Stop: .\start-trial.ps1 -Stop"
