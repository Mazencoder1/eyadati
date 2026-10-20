# Infrastructure readiness and go-live checklist

> **Status: nothing is provisioned, deployed or decided here.** This is what the owner and the infrastructure operator
> must decide, provide and verify, in order, before Clinic SaaS can go live. The application side is done and tested
> locally (plan §19–§21); everything below is **external**. Values in angle brackets are placeholders — no provider,
> region, domain, address, certificate, credential or price has been chosen. No item is a compliance claim.

Roles used below: **Owner** (business decisions, sign-off), **Legal** (Saudi legal/compliance counsel), **Operator**
(infrastructure engineer), **Security** (security reviewer; may be the operator plus an external tester), **On-call**
(the person who answers alerts).

## 1. Readiness by area

| Area | What is required | Why | Where it is set | Owner | How it is verified | Pre-go-live test |
|---|---|---|---|---|---|---|
| Hosting provider | A provider able to keep application, database, backups, logs and key material in the residency boundary decided below; managed SQL Server or a VM; a secret store; private networking | Health data is sensitive personal data; decision 3A-D1 | Provider contract | Owner (+ Legal) | Written decision with the comparison of Saudi-hosted options | — |
| Data residency | Decide what must stay in-Kingdom (default proposal: everything, including logs, backups and email data where possible) and which sub-processors are acceptable | 3A-D2; PDPL questions in plan §4 | Decision record in plan §19.2 | Owner + **Legal** | Legal review memo; provider data-location statements | — |
| Legal / compliance review | PDPL, health-sector rules, NCA/CST cloud rules, breach-notification duties | 3A-D10; incident-response.md leaves notification to Legal | — | **Legal** | Review completed with no open blocking item | — |
| Domain | One registrable domain `<domain>` (not a public suffix); `app.<domain>` (web) and `api.<domain>` (API); nothing else under it serves untrusted content | Refresh cookie is `SameSite=Strict` and host-only (plan §19.6) | Registrar / DNS | Owner → Operator | `dig app.<domain>` / `api.<domain>` resolve to the edge only | Sign in, reload, the session survives (refresh works) |
| HTTPS / TLS | Valid certificates for both names, auto-renewed; HTTP redirects to HTTPS | Cookies are `Secure`; HSTS is sent | Edge / load balancer | Operator | TLS scan (protocols, chain); HSTS header present | Expiry alert (monitoring A16) fires in staging |
| Reverse proxy / load balancer | Terminates TLS (or passes through), forwards `X-Forwarded-For`/`-Proto`, overwrites client-supplied forwarded headers, the API is not reachable around it | Correct client IP for rate limiting, lockout and audit (deployment-prerequisites §6) | Edge config | Operator | API has no public address of its own | Audit log of a staging login shows the caller's real IP, not the proxy's |
| Trusted-proxy config | `ForwardedHeaders__KnownProxies__N` or `__KnownNetworks__N` (exact edge range, never `0.0.0.0/0`) and `ForwardLimit` = trusted hops; or `NoProxy=true` | The API ignores forwarded headers from anyone else | API environment | Operator | Start-up guard accepts the configuration | A request with a forged `X-Forwarded-For` from outside is recorded with its real address |
| Access logs at the edge | Do **not** record query strings (patient searches carry mobile/national-ID numbers), or treat those logs as health data | sensitive-data-in-logs.md §3–4 | Edge / WAF / CDN / APM config | Operator | Inspect a sample access-log line after a patient search | Search for a test mobile number; it appears in no edge log |
| WAF / edge rate limits | Recommended for anonymous floods (P3-D23) | The API limiter is per process | Edge | Operator (decision: Owner) | Rules listed | Burst test in staging |
| SQL Server (production) | SQL Server 2022-compatible, private endpoint, encryption at rest (TDE or provider storage encryption), point-in-time backups | System of record | Provider | Operator | Server not reachable from the internet | Connect from outside fails |
| Connection string | `Server=<private host>;Database=<name>;User ID=<app login>;Password=<secret>;Encrypt=True;Connect Timeout=8` — no `TrustServerCertificate=True`, not `sa`, not a `ClinicSaaS_Dev*/Test*/E2E*` name | Start-up guard (deployment-prerequisites §2, §5) | Secret store → `ConnectionStrings__Default` | Operator | API starts (the guard lists any unsafe value by key) | — |
| Database principals | Application login (least privilege) and a separate migrator login; adapt `docs/database/security/least-privilege.sql` to the offering (contained users where `CREATE LOGIN` is not available) | P3-SEC-18 | DB | Operator (changes to grants: **owner approval**) | Application login cannot run DDL; `DELETE`/`UPDATE` on `AuditLogs` denied | Attempt DDL with the app login → refused |
| Schema | Apply `docs/database/001…005_*.sql` (idempotent) with the migrator, in order; `Database__MigrateOnStartup=false` | Reviewed scripts only (deployment-prerequisites §7) | Pipeline / operator | Operator | `__EFMigrationsHistory` lists exactly 001–005 | Staging applied from scratch, then the full browser suite |
| Email provider | A provider adapter behind `IEmailSender` (plan task 3D), sending domain with SPF/DKIM/DMARC, credentials in the secret store, data location per residency decision | Invitations, verification, password reset; the API refuses to start without one | `Email__Provider` + provider keys | Owner chooses, Operator configures, a developer adds the adapter | Staging delivers ar + en messages to allow-listed inboxes | Reset-password and invitation flows end to end in staging |
| Data Protection key ring | Absolute `DataProtection__KeyRingPath` on durable storage outside the database, shared by all instances, backed up separately | Encrypts platform admins' TOTP secrets (platform-admin-mfa.md §1) | Storage + API environment | Operator | Guard accepts; restart keeps admins able to sign in | Restart staging; MFA sign-in still works |
| MFA protection certificate | X.509 with private key: `DataProtection__CertificatePath` + `__CertificatePassword` (from the secret store) or `__CertificateThumbprint`; custody and renewal owner named | Protects the key ring at rest | Secret store / cert store | Operator + Security | Guard loads it; a wrong certificate makes the API refuse to start | Rotate a staging certificate (keep the old one for decryption) |
| JWT signing key | `Jwt__SigningKey` ≥ 32 random bytes from the secret store; rotation per deployment-prerequisites §4 | Signs access tokens | Secret store | Operator | Guard accepts | Three-step rotation rehearsed in staging |
| Bootstrap admin | `Bootstrap__SuperAdminEmail/Password` for the **first** start only, then removed | P3-SEC-17 | Secret store | Operator | Warning `4104` stops appearing after removal | First admin enrols MFA, saves recovery codes offline |
| Second platform admin | A second platform admin so one lost device never blocks the platform | platform-admin-mfa.md §2 | **No creation path exists in the product** — today only the bootstrap creates a platform admin | Owner (decision: build a creation path, or accept one admin + break-glass) | — | — |
| Break-glass procedure | Named approvers, out-of-band verification method, ticketing, practised once | Lost authenticator + lost recovery codes (platform-admin-mfa.md §3) | Runbook | Owner + Security | Tabletop exercise recorded | Run the reset once in staging |
| Backups | Point-in-time (proposed RPO ≤ 15 min), separate account/immutable storage, credentials unreachable from the app/CI, key ring backed up separately | backup-restore.md §2 | Provider | Operator | Backup job monitoring (alert A17) | — |
| Restore test | Full restore into an isolated environment within the proposed RTO (≤ 1 h DB, ≤ 4 h environment), with the verification list of backup-restore.md §4 | P3-SEC-03 | Isolated environment | Operator | Drill record with timings | **Required before go-live** |
| Logging | Central log store in the residency boundary, role-based access, retention (proposed 30–90 days); `Observability__ConsoleLogFormat=json` (already in the repo's Staging/Production settings) | Investigations, alerts | Provider | Operator | Log lines arrive with `Application`, `Environment`, `Version`, `EventId` | Search by a correlation id from a staging request |
| Monitoring + alerting | Rules A1–A17 of monitoring-and-alerting.md, uptime probe on `/health/live`, readiness on `/health/ready`, certificate expiry | Nothing alerts today | Provider | Operator + On-call | Each rule has a synthetic trigger | Every rule fired once in staging and reached on-call |
| On-call + contacts | Named on-call person(s), escalation path, the contacts table in incident-response.md | Someone must answer | Runbook | Owner | Table filled | Test page |
| Incident response | Runbook exercised (tabletop), breach-notification duties confirmed by Legal | incident-response.md | Runbook | Owner + Legal | Tabletop record | — |
| Deployment | Container images (or provider build) for web and API, built once per version; web image **per environment** (`NEXT_PUBLIC_API_URL` is baked in, P3-D20); pipeline with manual approval for production (plan task 3H) | Repeatable, auditable releases | CI/CD | Operator | Image digest recorded per release | Staging deployed from the same artifacts |
| Instance count | **Exactly one API instance** (rate limiter is per process, 3A-D7), or a recorded decision with `RateLimiting__AcceptPerInstanceLimits=true` | P3-SEC-09 | API environment | Owner | Guard refuses >1 instance otherwise | — |
| Rollback | Previous image redeployable; migrations backward-compatible (expand → migrate → contract); restore point before each migration | deployment-prerequisites §7 | Pipeline | Operator | Previous image tag kept | Rollback rehearsed in staging |
| Staging | Same topology as production, synthetic data only, same guards, allow-listed email | Plan gates G-STAGING / G-AUDIT | Provider | Operator | — | Full browser suite (ar/en, 4 widths, axe) passes against staging |
| Health checks | `/health/live` (process only, never touches SQL) → restart probe and external uptime; `/health/ready` (database) → load-balancer readiness, exposed only to the LB/monitoring if possible | deployment-prerequisites §9 | LB / orchestrator | Operator | Probes configured | Stop staging SQL: ready → 503, live stays 200, the LB drains |
| Penetration test | Independent test before go-live (P3-D24) | G-AUDIT | — | Owner + Security | Report; findings triaged | — |

## 2. Go-live sequence (owner's checklist)

Each line is done only when its evidence exists. Nothing here is automated.

**Decisions (Owner, with Legal)**

- [ ] Hosting provider and region chosen after comparing Saudi-hosted options (3A-D1); data-residency scope decided (3A-D2)
- [ ] Saudi legal/compliance review engaged and completed with no open blocking item (3A-D10)
- [ ] Email provider and sending domain chosen (P3-D4); delivery model confirmed (tolerant send — no outbox — or approve an outbox table, P3-D5)
- [ ] Log/monitoring provider and retention chosen (P3-D8); on-call person(s) named
- [ ] Domain registered; `app.<domain>` / `api.<domain>` reserved
- [ ] RPO/RTO and backup retention confirmed (P3-D11/D12)
- [ ] Instance count confirmed (one API instance) (P3-D14 / 3A-D7)
- [ ] WAF at launch yes/no (P3-D23); penetration tester chosen (P3-D24)
- [ ] Pending product/security decisions answered or explicitly deferred in writing: account enumeration / lockout policy (P3-D17), `RequireConfirmedEmail` for staff in production (P3-D19), token/code cleanup and audit retention (P3-D18, deletes data), personal data in search query strings (keep + edge log rules, or change the API contract), second platform admin path

**Build (Operator, then a developer for the email adapter)**

- [ ] Staging provisioned: network, edge/TLS, web + API, SQL Server (private), secret store, log store
- [ ] Email provider adapter added and tested (plan task 3D)
- [ ] Data Protection key-ring storage + protection certificate provisioned; custody recorded
- [ ] Database principals created (least-privilege script adapted and **approved**); scripts 001–005 applied with the migrator
- [ ] Configuration supplied (deployment-prerequisites §2); the API starts with no guard error
- [ ] Trusted-proxy settings match the real edge; client IP verified in the audit log
- [ ] Edge access logs exclude query strings (or are handled as health data)
- [ ] Alerts A1–A17 created; each fired once in staging and reached on-call
- [ ] Backups running; **restore drill passed within RTO** and recorded
- [ ] CI/CD builds once, deploys staging, runs the browser suite against it; rollback rehearsed

**Verify (Security, Owner)**

- [ ] Full browser suite (Arabic/English, 4 widths, axe) green against staging
- [ ] Multi-tab, refresh across reload, logout, password change, MFA sign-in verified on the real domains
- [ ] Independent penetration test done; no open BLOCKER/HIGH, or each accepted in writing
- [ ] Incident-response tabletop and break-glass rehearsal recorded
- [ ] Bootstrap admin enrolled MFA; recovery codes stored offline; `Bootstrap__*` removed (no `4104` warning)

**Production (Owner sign-off required for each)**

- [ ] Production provisioned from the same definitions as staging; restore point taken; scripts applied
- [ ] Smoke test with a synthetic, clearly isolated test clinic; probes and one alert test green
- [ ] Owner sign-off recorded in `docs/STATUS.md`; only then may the product be described as "in production" — still
      without a compliance claim unless formally verified
