# Monitoring and alerting

> **Status: application hooks implemented and tested (Phase 3C); no monitoring platform, dashboard, alert or on-call
> rotation exists.** The log/monitoring provider is undecided (plan decision P3-D8) and must sit inside the residency
> boundary the owner chooses (3A-D2). Thresholds below are **proposals** for the owner to confirm; nothing here is a
> compliance claim.

## 1. What the API emits

Logs go to stdout. In Staging/Production (`Observability__ConsoleLogFormat=json`, already set in the repository's
`appsettings.Staging.json` / `appsettings.Production.json`) every line is one JSON object:

```json
{"Timestamp":"…","Level":"Warning","MessageTemplate":"…","RenderedMessage":"…",
 "Properties":{"EventId":{"Id":4100,"Name":"SecurityAudit"},"AuditAction":"auth.account.locked","AuditResult":"Success",
   "AuditId":563,"AuditUserId":"…","AuditTenantId":"…","CorrelationId":"…","UserId":"…","TenantId":"…",
   "Application":"ClinicSaaS.Api","Environment":"Production","Version":"1.0.0+<commit>","SourceContext":"…"}}
```

Alert rules match on **`Properties.EventId.Id`** and named properties — never on message text. The ids are defined in
`backend/src/ClinicSaaS.Application/Abstractions/SecurityLogEvents.cs`; changing one means changing this page and the rules.

| Event id | Name | Level | Emitted when | Properties |
|---|---|---|---|---|
| 4100 | `SecurityAudit` | Warning or Information | A security-relevant audit row was **saved** (never for a rolled-back write) | `AuditAction`, `AuditResult`, `AuditId` (row in `AuditLogs`), `AuditEntityType`, `AuditEntityId`, `AuditUserId`, `AuditTenantId` |
| 4101 | `RateLimited` | Warning | A request was refused with 429 | `RateLimitPolicy` (`auth`, `refresh`, `sensitive`, or `global` when the endpoint has none; the global per-user/IP budget may be the one that refused), `RequestMethod`, `RequestPath` (no query string) |
| 4102 | `EmailDeliveryFailed` | Error | An email could not be handed to the provider after the data was saved | `Purpose`, `Domain` (recipient domain only), `FailureTypes` |
| 4103 | `TenantIsolationViolation` | Critical | A write that would have crossed clinics was blocked | exception |
| 4104 | `BootstrapSecretStillConfigured` | Warning | API start with `Bootstrap__*` still set although the account exists | — |
| 4105 | `MfaKeyRingUnreadable` | Error | A platform admin's TOTP secret could not be decrypted (key ring missing/replaced) | `UserId` |
| 4106 | `DatabaseTemporarilyUnavailable` | Warning | A transient database failure was answered with 503 + `Retry-After` | `Reason` |
| 4107 | `UnhandledException` | Error | An unexpected failure was answered with a generic 500 | exception |

**4100 classification.** *Warning* (alert-worthy): `auth.account.locked`, `auth.refresh.reuse_detected`,
`auth.mfa.challenge_failed`, `auth.mfa.recovery_code_used`, `auth.mfa.recovery_codes_regenerated`, `auth.mfa.disabled`,
`auth.mfa.reset`. *Information* (security trail): `auth.login.failed`, `auth.password.changed`,
`auth.password_reset.requested`, `auth.session.revoked`, `security.access_denied`, the other `auth.mfa.*` events,
`tenant.registered`, `tenant.suspended`, `tenant.reactivated`, `subscription.changed`, `role.created|changed|deleted`,
`staff.invited|disabled|enabled`, `auth.invitation.accepted`. **Clinical activity (patients, visits, prescriptions,
appointments) is never mirrored** — it stays in the audit table only. 4100 never carries audit details, client IP or user
agent; `AuditId` links to the full row for an investigation.

Also available: request lines (`HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed}`, path without
query string, Error level for 5xx), `/health/live` and `/health/ready` (deployment-prerequisites.md §9).

## 2. Proposed alert rules

Alert priority: **P1** page the on-call person now, **P2** page within working hours, **P3** ticket. An alert that turns out to be an incident is then classified S1–S3 under `incident-response.md` §0.
"Platform account" means one of the few platform admin user ids — keep that list in the rule (there is no API to list them).

| # | Rule | Condition (proposal) | Severity | First action |
|---|---|---|---|---|
| A1 | Tenant isolation violation | any 4103 | P1 | incident-response §7 (suspected breach); keep the logs |
| A2 | Platform admin MFA failures | 4100 `auth.mfa.challenge_failed` ≥ 3 in 15 min for one user, or ≥ 1 outside the admin's working hours | P1 | Password is known to someone: contact the admin out of band, reset the password, incident-response §1 |
| A3 | Platform admin recovery code used / codes regenerated / MFA disabled / MFA reset | any 4100 with those actions | P2 | Confirm with the admin out of band that it was them |
| A4 | Platform admin locked out | 4100 `auth.account.locked` for a platform account | P1 | As A2 |
| A5 | Account lockouts (clinic users) | 4100 `auth.account.locked` ≥ 10 in 15 min across users | P2 | Credential stuffing; review 4101 `auth` and edge/WAF logs for the source |
| A6 | Refresh-token reuse | any 4100 `auth.refresh.reuse_detected` → ticket; ≥ 5 in 1 h → P2 | P3/P2 | Token theft or a client bug; the family is already revoked; check the user's other sessions |
| A7 | Rate limiting on auth | 4101 with policy `auth` or `sensitive` ≥ 50 in 5 min | P2 | Brute force or a clinic behind one IP; compare with A5 |
| A8 | Email provider failing | 4102 ≥ 3 in 10 min | P2 | incident-response §5; invitations/resets are not queued (no outbox) — users must retry |
| A9 | MFA key ring unreadable | any 4105 | P1 | The key ring or its certificate is missing/replaced; restore it (platform-admin-mfa.md §1). Admins can still use recovery codes |
| A10 | Database degraded | 4106 ≥ 5 in 5 min, or `/health/ready` failing ≥ 2 min | P1 | incident-response §4 |
| A11 | Error rate | 4107 ≥ 5 in 5 min, or 5xx > 2 % of requests over 10 min | P2 | Correlation id → logs; consider rollback (incident-response §8) |
| A12 | API down | `/health/live` failing ≥ 2 min from an external probe | P1 | Restart; incident-response §10 |
| A13 | Bootstrap secret left behind | any 4104 | P3 | Remove `Bootstrap__*` from the secret store/environment |
| A14 | Permission probing | 4100 `security.access_denied` ≥ 20 in 10 min for one user | P3 | Possible misuse by a staff account or a UI bug; review the user's audit trail |
| A15 | Platform operator actions | any 4100 `tenant.suspended`, `tenant.reactivated`, `subscription.changed` | notify | Operator-error check (no platform UI yet; changes go through the API) |
| A16 | TLS certificate expiry | < 21 days | P3 | Renew (provider-specific) |
| A17 | Backup job failed / restore drill overdue | per backup-restore.md | P2 | backup-restore.md |

## 3. Testing the alerts (staging, before go-live)

Each rule needs a synthetic trigger in staging and a recorded "alert fired, reached the on-call person" result:

| Rule | How to trigger in staging |
|---|---|
| A2/A4 | Wrong TOTP codes for a staging platform admin |
| A3 | Sign in a staging admin with a recovery code |
| A5 | Wrong passwords for several staging users |
| A6 | Replay a rotated refresh token with a native client |
| A7 | Exceed the `auth` budget from one address |
| A8 | Point the staging email provider at an invalid host |
| A9 | Start a second staging instance with a different key ring (never in production) |
| A10/A12 | Stop the staging database / API |
| A13 | Leave `Bootstrap__*` set and restart |
| A1 | Not triggerable without a code defect; covered by the integration suites — verify the rule syntax only |

## 4. Not implemented (needs a decision or infrastructure)

Log/metric provider and its region (P3-D8), dashboards, alert delivery (pager/phone/email), on-call rotation and
contacts (incident-response.md "Contacts"), retention (proposed 30–90 days), access control on the log store, metrics and
tracing (OpenTelemetry — not added; would be a new dependency, plan §10.2), uptime probes, certificate monitoring.
