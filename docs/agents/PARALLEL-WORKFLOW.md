# Parallel Agent Development: Git Worktrees

Several Claude Code agents work at the same time. Each agent has its own git worktree and its own branch.
All work meets on `integration`, which is verified before anything reaches `main`.

```text
main  ◄── fast-forward only, via promote-to-main.ps1, after verify passes
  │
integration  ◄── Manager merges agent branches here (--no-ff), then runs verify.ps1
  ├── agent/backend      C:\Users\Yazee\clinic-worktrees\backend
  ├── agent/frontend     C:\Users\Yazee\clinic-worktrees\frontend
  ├── agent/qa-security  C:\Users\Yazee\clinic-worktrees\qa-security
  └── agent/database     C:\Users\Yazee\clinic-worktrees\database   (the only schema/migration owner)
```

| Role | Branch | Worktree | API port | Web port | Dev DB (LocalDB) |
|---|---|---|---|---|---|
| Manager | `integration` | `C:\Users\Yazee\clinic-worktrees\integration` | 5100 | 3100 | `ClinicSaaS_Dev_integration` |
| Backend | `agent/backend` | `C:\Users\Yazee\clinic-worktrees\backend` | 5110 | 3110 | `ClinicSaaS_Dev_backend` |
| Frontend | `agent/frontend` | `C:\Users\Yazee\clinic-worktrees\frontend` | 5120 | 3120 | `ClinicSaaS_Dev_frontend` |
| QA/Security | `agent/qa-security` | `C:\Users\Yazee\clinic-worktrees\qa-security` | 5130 | 3130 | `ClinicSaaS_Dev_qa_security` |
| Database | `agent/database` | `C:\Users\Yazee\clinic-worktrees\database` | 5140 | 3140 | `ClinicSaaS_Dev_database` |

The original folder (`OneDrive\Desktop\clinic-management-main`) keeps `main` checked out. It is read-only by convention: nobody works there.
The worktrees live outside OneDrive so that `node_modules`, `bin` and `obj` are not synced.

## Guardrails

| Rule | How it is enforced |
|---|---|
| Nobody commits to `main` | `pre-commit` hook. git also refuses to check out `main` in a second worktree. |
| `main` only moves after verification | `reference-transaction` hook blocks every update of `refs/heads/main` (commit, merge, ff, reset, `branch -f`, delete) unless it comes from `promote-to-main.ps1` |
| Agents stay inside their scope | `pre-commit` path allowlist per branch (see `scripts/agents/hooks/pre-commit`) |
| Migrations have one owner | `pre-commit`: `Persistence/Migrations/**` and `docs/database/**` can be committed only on `agent/database` (and `integration` for conflict resolution) |
| No migration applied by agents | `.claude/settings.json` denies `dotnet ef database update/drop` and `migrations remove`. `Program.cs` refuses `MigrateOnStartup` in Production. No production connection string exists in the repo. |
| Agents do not share a dev DB or ports | `scripts/agents/agent-env.ps1` (used by `run-api.ps1` / `run-web.ps1`). Integration tests already use `ClinicSaaS_Test_<guid>` per run. |
| No env/secret files committed | `pre-commit` (blocks `.env*` except `.env.example`, `*.pem`, `appsettings.*.local.json`) |
| An agent edits only its own worktree | Claude Code limits file writes to the session's working directory, and every agent definition names its worktree |

These are guardrails against accidents. The override `CLINIC_ALLOW_CROSS_SCOPE=1` exists for a single commit the Manager has approved.
The hooks live in the shared `.git/hooks` and apply to every worktree. After editing `scripts/agents/hooks/*`, re-run `sh scripts/agents/install-hooks.sh`.

## Running the agents

Each agent definition lives in `.claude/agents/<name>.md`. Every worktree has an untracked `.claude/settings.local.json`
with `"agent": "<name>"`, so a Claude Code session opened in that folder starts as that agent.

**Option A: one session per agent (recommended for real parallel work).**
Open each worktree in its own VS Code window and start Claude Code there:

```powershell
code C:\Users\Yazee\clinic-worktrees\integration   # Manager
code C:\Users\Yazee\clinic-worktrees\backend
code C:\Users\Yazee\clinic-worktrees\frontend
code C:\Users\Yazee\clinic-worktrees\qa-security
code C:\Users\Yazee\clinic-worktrees\database      # only when a schema change is approved
```

(With the CLI: `cd <worktree>; claude`, or `claude --agent backend`.)

**Option B: the Manager delegates through subagents.**
In the Manager session (integration worktree), ask for a goal. The Manager calls the `backend`, `frontend`, `qa-security`
and `database` subagents with the Agent tool. Independent tasks run in the background at the same time, and each subagent works
in its own worktree by absolute path. The integration worktree's `settings.local.json` adds the other worktrees to
`additionalDirectories` so the subagents may write there.

Use A for long, heavy tasks, because each agent keeps its own context. Use B for coordinated short tasks.

## Task flow

1. **User → Manager**: the goal (e.g. "Phase 5 step 9: authentication flow").
2. **Manager**: splits it into tasks in `docs/agents/TASKS.md` (ID, owner, scope, acceptance criteria, dependencies) and commits on integration.
   - No two agents get the same files at the same time.
   - Schema need → `database` first. API contract → `backend` first. Then `frontend`, then `qa-security`.
3. **Agent**: syncs with `git merge integration`, implements, runs `verify.ps1 -Scope <its scope>`, commits on its branch, reports.
4. **Manager**: merges into integration and runs the full `verify.ps1` (see below).
5. **QA/Security**: merges integration into its branch, tests and audits the integrated state, and reports defects to the Manager. The Manager routes them to the owner.
6. **User approves → Manager**: `promote-to-main.ps1`.

## Merging (Manager, integration worktree)

```powershell
git log --oneline integration..agent/backend          # review what is coming
git merge --no-ff agent/backend -m "merge: agent/backend - T-012"
powershell -File scripts/agents/verify.ps1            # build + all tests + lint + typecheck + frontend build
```

- If verify fails, undo with `git revert -m 1 <merge-sha>` (no history rewriting) and send the failure output to the owner.
- Merge order when several branches are ready: **database → backend → frontend → qa-security**, with verify after each one.
  Then a failure points to exactly one branch.
- After each successful merge, every other agent runs `git merge integration` in its own worktree before continuing.
  Short-lived divergence means small conflicts.

## Conflicts

1. **Prevention first**: scopes do not overlap except Domain/Infrastructure code between backend and database, and the Manager never runs those two on the same files at the same time.
   `docs/STATUS.md` and `docs/agents/TASKS.md` are edited only on integration by the Manager.
2. **During a merge into integration**:
   - Trivial (docs, imports, independent hunks): the Manager resolves, verifies, then commits.
   - Semantic (both sides changed the same behaviour): `git merge --abort`. The owning agent runs `git merge integration` in its own worktree,
     resolves it there with full context, re-verifies and reports again.
   - **Migrations / `AppDbContextModelSnapshot.cs`: never hand-merge.** Abort, and have the database agent merge integration
     into `agent/database` and regenerate the latest unapplied migration so the snapshot is rebuilt from the merged model.
3. `verify.ps1` refuses to pass with unresolved conflicts or leftover conflict markers.
4. Never "resolve" by deleting the other side's tests or weakening assertions.

## Protecting main

- `main` is updated only by `scripts/agents/promote-to-main.ps1`, run from a clean integration worktree. It runs the full
  verify gate, checks that `main` is an ancestor of `integration`, and fast-forwards `main`. There are no merge commits on main and no rewrites.
- The hooks block everything else. Claude Code settings deny `git checkout/switch main`, force pushes, and ask before
  `reset --hard`, `clean`, `rebase`, `branch -D` and promotion.
- **When a remote is added (GitHub)**: also enable branch protection on `main` (require a PR from `integration`, required status checks running
  the same commands as `verify.ps1`, no force pushes, no deletions) and make `integration` protected against force pushes.
  Local hooks can be bypassed with `--no-verify`. Server-side protection cannot.

## Starting a new cycle

After main is promoted, agent branches simply `git merge integration` (they are already ancestors or close to it).
To give an agent a clean slate, the Manager may ask the user to approve
`git -C <worktree> reset --hard integration`, but only after that branch is fully merged (`git branch --merged integration`).
