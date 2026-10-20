# Incident response runbook

> **Status: written, not yet exercised.** It uses only controls that exist in the code today, and marks every step that
> still needs infrastructure or a decision. It contains **no legal or compliance statement**: whether, to whom and by when a
> security incident or data breach must be reported (regulators, the Ministry of Health, affected people, clinics under
> contract) is a question for **Saudi legal/compliance review** and is deliberately left blank here — do not rely on this
> document for any notification duty.

## 0. Roles, severity, and the first ten minutes

| Role | Responsibility |
|---|---|
| Incident commander | Decides, records, approves any data change and any customer communication. One person, named at the start |
| Operator | Executes the technical steps; every command and its result is written to the incident log |
| Communications / owner | Customer and partner messages; engages legal/compliance counsel |

Severity: **S1** confirmed access to patient data by an unauthorised party, or loss of availability for all clinics;
**S2** suspected compromise or one clinic affected; **S3** contained, no data exposure.

First ten minutes, always: (1) open an incident log with a UTC timeline; (2) note the correlation id(s) from any error or log
line (`X-Correlation-Id`, `cid=` in logs); (3) **preserve** evidence before changing anything (export the relevant logs and
audit rows; do not delete, restart or restore over the evidence); (4) decide containment before investigation if patient
data may still be exposed.

Tools that exist today:

| Control | How |
|---|---|
| Suspend a clinic (ends **all** its sessions at once) | Platform API `POST /api/v1/platform/tenants/{id}/suspend` (platform permission `Platform.Tenants.Manage`); reactivate does **not** restore the sessions |
| Disable one staff member (ends their sessions in that clinic) | Clinic API `POST /api/v1/staff/{membershipId}/disable` |
| A user ends one of their own sessions | `DELETE /api/v1/auth/sessions/{id}`; changing the password ends all of them |
| Rotate the JWT signing key (invalidates every access token) | `deployment-prerequisites.md` §4 |
| Read the audit trail | `GET /api/v1/audit-logs` (clinic owners/managers), the `AuditLogs` table for operators (login failures, lockouts, token reuse, status changes, tenant switches, file access…) |
| Correlate | `cid`, `uid`, `tid` on every log line and the `X-Correlation-Id` header |
| **Break-glass SQL** (data change, **not** schema) | §9 — only with the incident commander's written approval, after taking a backup |

## 1. Suspected account compromise (one user)

1. Confirm in the audit log: unusual `LoginSucceeded` addresses/times, `RefreshTokenReuse` (a replayed token already ends the
   whole session), password changes you did not expect.
2. Contain: the user completes "forgot password" (or changes the password while signed in) — that ends every session of
   that user immediately; an operator cannot start it for them, so if the user cannot be reached quickly use the tools
   below. For a staff member the clinic owner can **disable** the seat. For a platform admin or when speed matters: §9
   (deactivate the account) and rotate credentials.
3. Check what the account did (audit log by user id, time range); list affected clinics/patients.
4. If clinical data was read or changed: treat as a data incident (§7).
5. Re-enable only after a verified password reset; the account is re-invited if the seat was disabled.

## 2. Tenant (clinic) compromise or abuse

1. Suspend the clinic via the platform API — every session of the clinic dies at once and its users get `TENANT_SUSPENDED`.
   (Reactivation later does not revive old sessions; everyone signs in again.)
2. Review the audit log of that tenant; disable individual seats before reactivating.
3. Cross-tenant suspicion (a user seeing another clinic's data)? That is **S1**: the application raises a `Critical`
   "Tenant isolation violation blocked" log line and answers 404; if any real cross-tenant read is suspected, suspend the
   affected clinics, preserve logs, and treat as a breach (§7).

## 3. Credential or secret leak

| Secret | Action |
|---|---|
| JWT signing key | Emergency rotation (`deployment-prerequisites.md` §4); review the audit log; revoke sessions if theft of sessions is possible (§9) |
| SQL credentials | Rotate the login in SQL Server and the secret store, restart; review database audit/firewall logs; if exposed to the internet treat as S1 |
| Email provider credentials | Rotate at the provider; review sent mail; expect abuse of password-reset mail |
| Bootstrap platform password | Rotate it by resetting the admin's password; remove `Bootstrap__*` from the environment |
| A secret committed to Git | Rotate it first (history rewriting does not un-leak it); CI runs gitleaks on full history and fails on findings |
| A refresh token or access token (one user) | §1 |

## 4. Database outage

Signals: `503 TEMPORARILY_UNAVAILABLE` with `Retry-After`, `/health/ready` = 503 while `/health/live` = 200, warnings
`Database temporarily unavailable`.

1. Confirm at the provider (instance state, failover in progress, storage, connection limit, a network rule change).
2. The API already retried connection opens (and plain reads) briefly; **writes are never retried**, so clinics see an
   honest "try again" message. No data is queued — tell clinics that the last action may or may not have gone through.
3. If the database is lost or corrupt: stop the API, follow `backup-restore.md` (the restore is **not yet implemented
   anywhere** — until a drill has passed, assume no recoverability).
4. After recovery: check `/health/ready`, smoke test, and review the audit log for the window.

## 5. Email provider outage

Signals: `Email delivery failed for … to *@domain: <exception types>` at Error level. Registration, invitations and password
resets still succeed (the message is sent after the commit and failures are logged, not raised); the email itself is not
queued, so it is **lost**.

1. Check the provider's status and the credentials; do not log into mailboxes to read links.
2. Tell clinics: new users can use "resend verification", owners can resend invitations, users can repeat "forgot password"
   once the provider is back.
3. Repeated failures with the right credentials may be abuse or a sending-domain problem (SPF/DKIM/DMARC): contact the provider.

## 6. Suspicious access

Signals: login-failure and lockout spikes (audit `LoginFailed`/`AccountLocked`), `429 RATE_LIMITED`, many tenants touched by
one address, platform actions at odd hours, `RefreshTokenReuse`.

1. Identify the address(es) (`cid`/audit `IpAddress`; only trustworthy if the trusted-proxy configuration was verified).
2. Block at the edge (WAF/load balancer) — the application has no IP block-list. Lockout (5 failures → 15 min) already
   protects accounts, and also lets an attacker lock a victim out; consider notifying the user.
3. If a valid account was used: §1. If a platform account: S1, §9 immediately.

## 7. Suspected data breach

1. Contain (§1/§2/§3) and **preserve** evidence (logs, audit rows, database snapshot) before any cleanup.
2. Establish scope from the audit log and logs: which accounts, which clinics, which records, what time range, read vs
   write. Do not guess: if the logs cannot answer it, say so.
3. **Legal notification requirements, deadlines and the content of notices: requires Saudi legal/compliance review.**
   Engage counsel at the start of any S1/S2 involving patient data; this runbook states no duty or deadline.
4. Communicate only what the incident commander and counsel approve; keep a record of who was told what and when.
5. After: root cause, fix, regression test, update this runbook.

## 8. Application rollback

1. Deploy the previous immutable image (the web and API are versioned separately); database migrations are forward-only and
   expand-first, so the previous version still works with the current schema.
2. If the new version also wrote bad data: assess before restoring (restore loses data back to the restore point).
3. Verify: `/health/live`, `/health/ready`, sign-in, a booking, the browser smoke suite against staging first where possible.
4. Record the cause; do not re-deploy the faulty build until a test reproduces and fixes it.

## 9. Emergency session revocation and account deactivation

Choose the narrowest tool that contains the incident:

| Scope | Tool |
|---|---|
| One user, all devices | Password reset or change (ends all of that user's sessions) |
| One clinic | Platform API suspend (ends all its sessions) |
| One seat | Clinic API disable |
| All access tokens, immediately, no sessions touched | Rotate the signing key — clients simply refresh |
| **All sessions of all users** | Break-glass data change below |
| One account that must not sign in at all (e.g. a compromised platform admin) | Break-glass data change below |

Break-glass statements are **data changes, not schema changes**. They need the incident commander's written approval and a
verified restore point first, are run by a named operator with the break-glass database role, and are recorded in the
incident log. They take effect immediately because every request checks that its session is live and its user active.

```sql
-- End every live session (all users, all clinics, platform included). Everyone signs in again.
UPDATE RefreshTokens SET RevokedAtUtc = SYSUTCDATETIME(), RevokedReason = 'incident' WHERE RevokedAtUtc IS NULL;

-- Stop one account from signing in or using any token it holds (reverse with IsActive = 1 after the investigation).
UPDATE Users SET IsActive = 0 WHERE NormalizedEmail = UPPER(N'<email>');
```

Neither statement has ever been run against any real environment; rehearse them in staging and keep them out of
application code and scripts that run automatically.

## 10. Service restoration

1. Root cause understood and contained; changes reviewed.
2. Restore in stages: platform admins → one pilot clinic → everyone. Reactivate suspended clinics deliberately (their users
   sign in again; old sessions stay dead).
3. Keep heightened monitoring for at least 24 hours (auth failures, 5xx, readiness, rate limits, email failures).
4. Customer communication approved by the incident commander (and counsel for S1/S2 with patient data).
5. Post-incident review within a week: timeline, what worked, what did not, actions with owners; update the runbook,
   alerts and tests.

## Contacts (to be filled in before go-live)

Incident commander and deputy · database/hosting provider support · email provider support · legal/compliance counsel ·
owner. **Not defined yet.**
