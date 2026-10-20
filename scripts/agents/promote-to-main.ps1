# The ONLY sanctioned way to advance main.
#  1. Must run from the integration worktree, with a clean tree.
#  2. Runs the full verification gate (backend + frontend).
#  3. Fast-forwards main to the verified integration commit (no merge commits on main, no rewrites).
# The reference-transaction hook blocks every other update of refs/heads/main.
#
# Usage (integration worktree):  powershell -File scripts/agents/promote-to-main.ps1
$ErrorActionPreference = 'Stop'

$branch = (git symbolic-ref --short -q HEAD).Trim()
if ($branch -ne 'integration') { throw "Run this from the integration worktree (current branch: $branch)." }
if (git status --porcelain) { throw 'Integration worktree is not clean. Commit or discard changes first.' }

$candidate = (git rev-parse HEAD).Trim()

& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot/verify.ps1" -Scope all
if ($LASTEXITCODE -ne 0) { throw 'Verification failed. main was NOT updated.' }

if ((git rev-parse HEAD).Trim() -ne $candidate) { throw 'integration moved during verification. Re-run.' }

git merge-base --is-ancestor main $candidate
if ($LASTEXITCODE -ne 0) { throw 'main is not an ancestor of integration (main diverged). Merge main into integration, verify, then retry.' }

# Locate the worktree that has main checked out (the original repo folder), if any.
$mainWorktree = $null; $current = $null
foreach ($line in (git worktree list --porcelain)) {
    if ($line -like 'worktree *') { $current = $line.Substring(9) }
    if ($line -eq 'branch refs/heads/main') { $mainWorktree = $current }
}

$env:CLINIC_ALLOW_MAIN = '1'
try {
    if ($mainWorktree) {
        if (git -C $mainWorktree status --porcelain --untracked-files=no) { throw "main checkout at $mainWorktree has local modifications. Nobody may work there - clean it first." }
        git -C $mainWorktree merge --ff-only $candidate
    } else {
        git update-ref refs/heads/main $candidate (git rev-parse main).Trim()
    }
    if ($LASTEXITCODE -ne 0) { throw 'Fast-forward of main failed.' }
} finally {
    Remove-Item Env:CLINIC_ALLOW_MAIN -ErrorAction SilentlyContinue
}

Write-Host "main promoted to $($candidate.Substring(0,7)) (verified)." -ForegroundColor Green
