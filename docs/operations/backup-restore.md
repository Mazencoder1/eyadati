# Backup and restore

> ## NOT IMPLEMENTED — OPERATIONAL REQUIREMENT
>
> Nothing in this repository takes, stores, verifies or restores a backup. There is no production database, no backup job,
> no storage account and no restore has ever been tested. This page is the **requirement** a production deployment must meet
> before go-live (plan gate G-STAGING: "restore drill passed within the proposed RTO"). The numbers are **proposals** for the
> owner to accept or change; retention periods in particular depend on a Saudi legal/compliance review and are not stated
> here as obligations.

## 1. What must be protected

| Data | Where | Notes |
|---|---|---|
| SQL Server database (clinical, appointments, patients, users, sessions, audit log, subscriptions) | the chosen SQL offering | The only system of record. Contains personal health data |
| Application configuration and secrets | secret store | **Not** in the database backup. Needs its own recovery (soft delete/versioning, documented break-glass). Loss of the JWT key is harmless to data (everyone's next call refreshes) |
| Data Protection key ring (decrypts the platform admins' TOTP secrets) + its protection certificate | persisted directory + secret store | **Not** in the database backup, and **backed up separately**: a database restored without its key ring cannot decrypt a single MFA secret (every platform admin would need a break-glass reset). See [platform-admin-mfa.md](platform-admin-mfa.md) |
| Container images / build artefacts | registry | Immutable, versioned, so any previous version can be redeployed |
| Attachment files | not applicable | Attachments are disabled (decision 3A-D9). If ever enabled: object versioning + soft delete, same residency and retention, restored to a consistent point with the database |

## 2. Strategy (requirement)

| Item | Requirement |
|---|---|
| Backup types | Full (weekly), differential (daily), transaction log (every 5–15 min) — **or** the managed service's automated backups with point-in-time restore. Either way a restore to any minute inside the window must be possible |
| Proposed RPO | **≤ 15 minutes** of data loss |
| Proposed RTO | **≤ 1 hour** for a database point-in-time restore; **≤ 4 hours** to rebuild the whole environment in the same region from code and backups |
| Retention | Operational window of 7–35 days of point-in-time restore; longer-term copies (monthly) for a period set by the owner after legal/compliance review |
| Encryption | Backups encrypted at rest (service-managed or customer-managed keys) and in transit; keys not stored next to the backups |
| Location | Inside the data-residency boundary chosen by the owner (decisions 3A-D1/D2). Final residency requirements need Saudi legal review |
| Separation | Backups live in a **different account/subscription (or immutable/WORM storage)** from production, and the credentials that can *delete or alter* backups are different from — and unreachable with — the application's, the migrator's and the deployment pipeline's credentials. A compromised application or CI credential must not be able to destroy the backups |
| Access | Named operators only, with MFA, every restore access logged |
| Monitoring | Alert on a failed or missed backup, on log-backup lag beyond the RPO, and on storage nearly full |

## 3. Restore procedure (to be rehearsed, not yet executed)

Restore **into a new, isolated database first**; never restore over production without an explicit incident-commander decision.

1. **Declare and record**: time, reason, target restore point (a timestamp, or "latest"), decision-maker. Preserve the
   current database (or a copy) as evidence if corruption or intrusion is suspected.
2. **Stop writes**: put the API into maintenance (take instances out of the load balancer / scale to zero). Do not leave
   the application running against a half-restored database.
3. **Restore** the chosen point into a new database name using the backup role (not the application login).
4. **Verify the restore before using it** (§4).
5. **Switch**: point the application's connection string at the restored database (or rename after approval), start one
   instance, check `/health/ready`, then scale up.
6. **Revoke sessions if the restore point is before an incident or before a revocation**: the database stores which refresh
   tokens are revoked, so restoring to an earlier point can make revoked sessions live again. After any restore to a point
   earlier than a security event (password change, member disabled, clinic suspended, token-reuse detection), revoke the
   affected sessions (or all sessions — `incident-response.md` §9) before reopening.
7. **Reconcile**: the people and clinics whose data was created between the restore point and the failure (≤ RPO) must be
   told what to re-enter. Scheduled appointments in that window are the main customer-visible loss.
8. **Review**: record the timeline, the achieved RPO/RTO, and what to improve.

## 4. Restore verification (every restore, and every drill)

- The restore completes without error and `DBCC CHECKDB` (or the offering's equivalent integrity check) is clean.
- The EF migration history matches expectations (`__EFMigrationsHistory` lists migrations 001–004 or the current set) and
  `dotnet ef migrations has-pending-model-changes` is clean against the code version being deployed.
- Smoke checks on **synthetic or the restored data under proper access control**: row counts per main table are plausible
  for the restore point; a platform admin can sign in; a clinic owner can sign in and read their patients; one appointment can
  be read; `/health/ready` is healthy; tenant isolation holds (a user of clinic A cannot read clinic B).
- Record: restore point requested vs obtained (the achieved RPO), elapsed time (the achieved RTO), who ran it, the result.

## 5. Drills

Monthly automated restore into an isolated environment with the checks above; a quarterly full disaster-recovery exercise
timed against the RTO (including secrets recovery and redeploying the previous image). A backup that has never been restored
is not a backup — until the first successful drill is recorded, treat the system as having **no recoverability**.

## 6. What the application does and does not help with

- Helps: the schema is migration-controlled and idempotent scripts exist; `/health/ready` reports database reachability;
  migrations never run automatically in production; the audit log is append-only by database grant.
- Does **not** do: schedule or verify backups, encrypt or ship them, run restores, test them, or alert on failure. All of that
  is infrastructure that still has to be built (plan phases 3I/3J) and verified in staging.
