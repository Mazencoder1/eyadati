# Phase 3 — Production & Cloud **Phase 3C: each start with the secrets still set after the account exists logs warning event 4104 (never the value); removal stays an operator step ([§21](#21-phase-3c--security-operations-and-readiness-2026-10-08)).** **Fixed in Phase 3C: refused with 403 `TENANT_SUSPENDED` before anything changes; the link works after reactivation ([§21](#21-phase-3c--security-operations-and-readiness-2026-10-08)).** **Closed in Phase 3C as accepted platform behaviour (no keyboard trap, page content stays inert) — no code change ([§21](#21-phase-3c--security-operations-and-readiness-2026-10-08)).** **Reproduced and fixed in Phase 3C: tabs now take turns refreshing (Web Locks); browser regression test ([§21](#21-phase-3c--security-operations-and-readiness-2026-10-08)).** ||||Plan

- Status: **PLAN FOR REVIEW**. Nothing in this document has been implemented. No application code, database,
  migration, infrastructure or CI file was changed to write it.
- Date: 2026-10-08. Baseline: branch `claude/phase-2-gap-audit-s02iba` at `435864a` (MVP acceptance gate: PASS WITH
  FINDINGS).
- Scope: analysis of the existing MVP and a gated implementation plan for a first production release.
- The product is **not production-ready** and this plan makes **no compliance claim** (PDPL, NCA, CST, MOH, NHIC,
  ZATCA, SCFHS). Every legal point below is an input for a Saudi legal/compliance specialist, not a conclusion.
- **Phase 3A update (2026-10-08):** the owner has decided the items listed in [§19](#19-phase-3a-decisions-and-security-hardening-2026-10-08)
  and the session-revocation and refresh-token hardening were implemented (no database change).
- **Phase 3B update (2026-10-08):** production-readiness work that needs no deployment, provider or schema change was
  implemented and documented ([§20](#20-phase-3b--production-readiness-without-deployment-2026-10-08), [`operations/`](operations/README.md)).
  Everything else in this document is still a plan. The text of §1–§18 below is the original plan and is **not** rewritten; where Phase 3A
  changed something, a "Phase 3A" note says so and §19 is authoritative.
- Current state of the project: [`STATUS.md`](STATUS.md). Earlier production notes:
  [`security/PHASE2-MVP-completion.md`](security/PHASE2-MVP-completion.md#production-readiness-review-read-only-p3).

Legend used throughout:

| Tag | Meaning |
|---|---|
| **[TECH]** | Technical requirement — implemented in code, configuration or infrastructure |
| **[OPS]** | Operational requirement — a process, runbook, role or recurring activity |
| **[LEGAL]** | Legal/compliance question — needs review by a Saudi legal/compliance specialist before go-live |
| **[DECISION]** | Needs an explicit owner decision (collected in [§16](#16-decisions-requiring-approval)) |
| **[DB-APPROVAL]** | Would change schema, indexes, constraints, seed or stored data — needs separate explicit approval |

---

## Contents

1. [Current architecture](#1-current-architecture)
2. [Production target architecture](#2-production-target-architecture)
3. [Security assessment](#3-security-assessment)
4. [Saudi / privacy considerations](#4-saudi--privacy-considerations)
5. [Infrastructure requirements](#5-infrastructure-requirements)
6. [Environment strategy](#6-environment-strategy)
7. [Database strategy](#7-database-strategy)
8. [Backup / DR strategy](#8-backup--disaster-recovery-strategy)
9. [CI/CD strategy](#9-cicd-strategy)
10. [Observability](#10-observability)
11. [Email](#11-email)
12. [Attachments](#12-attachments)
13. [Performance / scaling](#13-performance--scaling)
14. [Cost model](#14-cost-model)
15. [Risks](#15-risks)
16. [Decisions requiring approval](#16-decisions-requiring-approval)
17. [Phase 3 implementation roadmap](#17-phase-3-implementation-roadmap)
18. [Acceptance gates](#18-acceptance-gates)

19. [Phase 3A — decisions and security hardening (2026-10-08)](#19-phase-3a-decisions-and-security-hardening-2026-10-08)
20. [Phase 3B — production readiness without deployment (2026-10-08)](#20-phase-3b--production-readiness-without-deployment-2026-10-08)
21. [Phase 3C — security operations and readiness (2026-10-08)](#21-phase-3c--security-operations-and-readiness-2026-10-08)

Appendix A — [Documentation vs code conflicts found](#appendix-a--documentation-vs-code-conflicts-found)

---

## 1. Current architecture

Everything in this section was read from the source on `435864a`, not from older documents. Where a document
disagrees with the code, see [Appendix A](#appendix-a--documentation-vs-code-conflicts-found).

### 1.1 Components

```text
Browser (Arabic RTL / English LTR)
   │  HTTPS (assumed; nothing terminates TLS yet)
   ├──► Next.js 16.4 web app (frontend/)          Node server: per-request CSP nonce (src/proxy.ts)
   │        renders UI; holds no secrets; calls the API from the browser
   │
   └──► ASP.NET Core 10 API (backend/src/ClinicSaaS.Api)   cross-origin, CORS with credentials
            │  EF Core 10, SqlClient
            ▼
        SQL Server (LocalDB in development, SQL Server 2022 container in CI)
```

There is no reverse proxy, cache, queue, object store, email provider or background worker today.

### 1.2 Backend

| Area | What the code does | Where |
|---|---|---|
| Layers | Domain → Application → Infrastructure → Api (Clean Architecture); thin controllers | `backend/src/*` |
| Culture | Process culture forced to Invariant (a Saudi server's `ar-SA` would switch to the Hijri calendar) | `Program.cs` top |
| Authentication | JWT bearer, HS256 only, issuer/audience/lifetime/signature validated, 30 s clock skew, 15 min lifetime (`Jwt:AccessTokenMinutes`) | `Program.cs`, `Infrastructure/Security/JwtTokenService.cs` |
| Signing key | `Jwt:SigningKey` ≥ 32 bytes; startup refuses an empty key outside Development; Development generates an ephemeral key | `Program.cs` |
| Token claims | `sub`, `email`, `name`, `jti`, `prole`, `sid` (= refresh-token family id), `tid`, `mid`, one `perm` per permission, one `br` per branch | `JwtTokenService.cs` |
| Refresh tokens | 256-bit opaque, stored as SHA-256 hash, 14 days (`Auth:RefreshTokenDays`), rotated on every refresh, family revoked on reuse | `Application/Auth/AuthService.cs` |
| Refresh transport | Web: cookie `rt`, `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`. Native clients (`X-Client-Type: mobile`): token in the response body | `Api/Controllers/AuthController.cs` |
| Session end | Logout, password change, password reset, invitation password, tenant switch, session revoke and suspension all revoke refresh tokens. Access tokens already issued stay valid until expiry (GD-03) | `AuthService.cs`, `PlatformTenantService.cs` |
| Per-request checks | For clinic tokens: membership still `Active` (GD-02) and tenant access (`Full` / `ReadOnly` / `Suspended`), two small queries per request. Platform tokens: no per-request check | `Api/Infrastructure/TenantAccessMiddleware.cs`, `Application/Saas/TenantAccess.cs` |
| Lockout | 5 failed attempts → 15 min lockout per account (database, so it holds across instances) | `AuthService.cs`, `appsettings.json` |
| Passwords | ASP.NET Core Identity `PasswordHasher` (PBKDF2-HMAC-SHA512, 100k iterations) | `Infrastructure/Services/CoreServices.cs` |
| One-time links | Password reset 30 min, email verification 48 h, invitation; hashed, single use, newest only | `AuthService.cs` |
| Authorization | Fallback policy = authenticated; permission policies (`[HasPermission]`); tenant/branch/own-doctor scoping in services | `Api/Authorization/`, `Application/Common/ClinicContext.cs` |
| Tenant isolation | Global query filter on every `ITenantOwned` entity (deny when no tenant); SaveChanges guard (force/verify TenantId); stored TenantId is a concurrency token so UPDATE/DELETE include `AND TenantId = @tenant`; composite FKs in the schema | `Infrastructure/Persistence/AppDbContext.cs` |
| Concurrency | `rowversion` on mutable tables; `sp_getapplock` (exclusive, transaction-owned, 10 s timeout) per doctor for booking/reschedule, per clinic-day for walk-in tokens, per visit for completion vs prescription; filtered unique index `UX_Appointments_ActiveSlot` as backstop | `AppDbContext.AcquireLockAsync`, `AppointmentService.cs`, `VisitService.cs`, `PrescriptionService.cs` |
| Transactions | `InTransactionAsync` (ReadCommitted by default; nested calls join the outer one) | `AppDbContext.cs` |
| SQL resilience | `CommandTimeout(30)`; **no** `EnableRetryOnFailure` (deliberate, see code comment) | `Infrastructure/DependencyInjection.cs` |
| Rate limiting | ASP.NET Core in-process limiter: `auth` 10/min/IP, `refresh` 120/min/IP, `sensitive` 5/15 min/IP, global 300/min per user (or per IP when anonymous); 429 with `Retry-After` | `Api/Infrastructure/RateLimiting.cs` |
| Client IP | `UseForwardedHeaders` trusts `X-Forwarded-For/Proto` only from `ForwardedHeaders:KnownProxies` (IP list; no network ranges) | `Program.cs` |
| CORS | Explicit origins from `Cors:AllowedOrigins`, credentials allowed, fixed methods/headers | `Program.cs` |
| Security headers | API: nosniff, DENY, `default-src 'none'` CSP, no-referrer, COOP, Permissions-Policy, `Cache-Control: no-store`; HSTS outside Development; HTTPS redirection | `Api/Infrastructure/Middleware.cs`, `Program.cs` |
| Errors | Unified JSON error body with localized message + correlation id; 500 shows a generic message; cross-tenant write → 404 + `LogCritical` | `Api/Errors/GlobalExceptionHandler.cs` |
| Logging | Serilog, **console only, text template** with `cid`, `uid`, `tid`; request logging (Error for 5xx); EF/ASP.NET at Warning | `Program.cs`, `Middleware.cs` |
| Correlation | Accepts a safe inbound `X-Correlation-Id` (8–64 `[A-Za-z0-9-]`) or uses the trace id; echoed on every response | `Middleware.cs` |
| Audit log | `AuditLogs` table (append-only by DB grant), action, entity, result, tenant, user, IP, UA, correlation id, details ≤ 2000 chars | `CoreServices.cs` (`AuditLogger`) |
| Health | `/health/live` (process), `/health/ready` (EF `DbContext` check); anonymous | `Program.cs` |
| Caching | `AddOutputCache()` in memory; used only for the public plan catalog (300 s) | `PlansController.cs` |
| Email | `IEmailSender` abstraction. `DevMailbox` (files on disk) allowed **only** in Development; `NotConfiguredEmailSender` otherwise; the API **refuses to start** outside Development/Testing without a real provider. Emails are sent synchronously inside the request, after the database commit | `Infrastructure/Notifications/`, `Program.cs`, `AuthService.cs`, `StaffService.cs` |
| Attachments | Full API (upload, list, metadata, download) with permission + tenant/branch/doctor scoping, size limit, SHA-256, and a scan state machine (`Pending → Scanning → Clean/Rejected/Failed`). Only `Clean` is downloadable; **no scanner exists, so every file stays quarantined**. Storage is `LocalDiskFileStorage`, refused at startup outside Development/Testing. Hidden in the UI (D3) | `Application/Attachments/`, `Infrastructure/Storage/`, `Program.cs` |
| Background work | **None** (no `IHostedService`/`BackgroundService`). No cleanup of expired refresh tokens or verification codes | — |
| Migrations | EF migrations 001–004; idempotent SQL scripts in `docs/database/`; `Database:MigrateOnStartup` only in Development/tests and refused in Production | `Persistence/Migrations/`, `Program.cs` |
| Bootstrap | First platform super admin from `Bootstrap:*` config on startup (no-op when unset) | `Application/Platform/PlatformBootstrap.cs` |
| Feature flags | `FeatureFlags:DisabledFeatures` environment-wide switch over plan features | `Application/Saas/SaasServices.cs` |
| OpenAPI | Mapped only in Development | `Program.cs` |
| Host filtering | `AllowedHosts: "*"` | `appsettings.json` |

### 1.3 Frontend

| Area | What the code does | Where |
|---|---|---|
| Stack | Next.js 16.4 (App Router), React 19.2, TypeScript, Tailwind v4, `next-intl` (ar default, en), IBM Plex Sans Arabic | `frontend/package.json` |
| Rendering | Needs a Node.js server: `src/proxy.ts` sets a per-request CSP nonce, so pages are rendered dynamically (no static export) | `src/proxy.ts` |
| API calls | All from the browser through one `ApiClient` (`src/lib/api/client.ts`) to `NEXT_PUBLIC_API_URL`; no server-side API calls, no Next.js API routes, no server secrets | `src/lib/api/` |
| Tokens | Access token in memory only; refresh cookie never visible to JS; single-flight refresh per tab; one retry after a 429 on refresh | `client.ts` |
| CSP | `default-src 'self'`, nonce + `strict-dynamic` scripts, `style-src 'self' 'unsafe-inline'`, `connect-src 'self' <api origin>`, `frame-ancestors 'none'`, `upgrade-insecure-requests` when the API is HTTPS | `src/proxy.ts` |
| Other headers | nosniff, DENY, `strict-origin-when-cross-origin`, Permissions-Policy, COOP; HSTS (2 years, includeSubDomains) in production builds | `next.config.ts` |
| Static assets | `_next/static` served by the Next.js server (content-hashed, cacheable); fonts self-hosted | `next.config.ts`, `proxy.ts` matcher |
| Config | One public variable `NEXT_PUBLIC_API_URL` (inlined at build time) | `frontend/.env.example` |

### 1.4 API ↔ frontend coupling that matters for deployment

1. **Same-site requirement.** The refresh cookie is `SameSite=Strict` on the API origin. A browser sends it on the
   frontend's `fetch` only if the web app and the API are **same-site** (same registrable domain, e.g.
   `app.example.sa` and `api.example.sa`, or one origin with the API under a path). On two unrelated domains, sign-in
   would work but every reload would sign the user out. This constrains DNS/topology ([§2.3](#23-domain-and-origin-layout)).
2. **Build-time API URL.** `NEXT_PUBLIC_API_URL` is compiled into the frontend bundle and into the CSP. One image
   per environment, or a change to runtime configuration (a small code change; [§9](#9-cicd-strategy)).
3. **CORS** must list the exact production web origin.

### 1.5 CI today

`.github/workflows/ci.yml` (checks only, nothing deployed): backend restore/build (Release)/unit/integration tests
against a throw-away SQL Server 2022 container, EF `has-pending-model-changes`, vulnerable NuGet check; frontend
`npm ci`/lint/typecheck/vitest/build/`npm audit --omit=dev --audit-level=high`; gitleaks over full history. The
Playwright browser suite (`frontend/e2e`, 47 tests) runs locally only.

---

## 2. Production target architecture

### 2.1 Topology (first production release)

```text
                    Internet
                       │
               DNS (authoritative, DNSSEC if supported)
                       │
        ┌──────────────▼──────────────┐
        │ Edge: TLS termination,       │  managed load balancer / reverse proxy
        │ HTTP→HTTPS, HSTS, WAF rules  │  (optional WAF; access logs WITHOUT query strings)
        │ request size limits          │
        └──────┬───────────────┬──────┘
               │               │
     app.<domain>          api.<domain>          (same registrable domain — §1.4)
               │               │
   ┌───────────▼───┐   ┌───────▼────────────┐
   │ Next.js        │   │ ASP.NET Core API   │  ≥ 2 instances for availability
   │ (Node, ≥2 inst)│   │ stateless          │──────► Email provider (HTTPS API or SMTP+TLS)
   └───────────────┘   └───┬──────────┬─────┘
                           │          │
                ┌──────────▼───┐  ┌───▼──────────────────┐
                │ SQL Server    │  │ Distributed limiter   │  (Redis-compatible or DB-backed;
                │ managed or    │  │ store — only when ≥2  │   §3.4 / Phase 3F)
                │ self-managed, │  │ API instances)        │
                │ private net,  │  └───────────────────────┘
                │ TDE, backups  │
                └───────────────┘
   Cross-cutting: secret store · log store · metrics/alerts · uptime probe · backup storage (separate account/region)
```

- The API and SQL Server sit on a private network; SQL Server has no public endpoint.
- The API is stateless except for the in-memory rate limiter and output cache (plan catalog only). With more than
  one instance, the limiter must become distributed (or be accepted as per-instance) — see [§3.4](#34-distributed-rate-limiting).
- No sticky sessions are needed: the access token is self-contained and the refresh token is in the database.

### 2.2 Component classification

| Component | First production release | Recommended | Optional later |
|---|---|---|---|
| DNS + TLS certificates (automatic renewal) | **Required** | DNSSEC, CAA records | — |
| Reverse proxy / load balancer with TLS termination | **Required** | HTTP/2, request size/timeouts tuned | HTTP/3 |
| WAF | — | **Recommended** (managed rule set, rate rules at the edge) | Bot management |
| Next.js server (Node 22 LTS or newer LTS) | **Required** (1 instance minimum) | 2 instances | CDN in front of `_next/static` |
| ASP.NET Core API | **Required** (1 instance minimum) | 2 instances (zero-downtime deploys) | Autoscaling |
| SQL Server | **Required** | Managed service with automated backups + PITR, zone redundancy | Read replica for reports |
| Secret store | **Required** | Managed key/secret vault with access audit | HSM-backed keys |
| Email provider | **Required** (startup refuses without one) | Dedicated sending domain with SPF/DKIM/DMARC | Separate transactional/marketing streams |
| Centralized logs (structured) | **Required** | Retention + access control + PHI-safe fields | SIEM |
| Uptime probe + alerting | **Required** | Paging/on-call rota | Status page |
| Metrics / tracing | — | **Recommended** (OpenTelemetry) | Distributed tracing UI |
| Backups to separate storage | **Required** | Immutable/WORM copy, second region in-Kingdom if available | Cross-provider copy |
| Distributed rate-limit store | Required **only** if >1 API instance and per-instance limits are rejected | **Recommended** with 2 instances | — |
| Object storage + malware scanning | — (attachments hidden, D3) | Only when the owner enables attachments | — |
| CDN | — | — | Optional |
| Background worker | — | Recommended once an email outbox or cleanup jobs exist | Separate worker process |

### 2.3 Domain and origin layout

**[DECISION]** P3-D13.

| Option | Description | Pros | Cons |
|---|---|---|---|
| **A. Sibling subdomains** (recommended) | `app.<domain>` + `api.<domain>` | Works with the current cookie as is (same-site); clear separation; CORS already supports it | Two certificates/hosts (or one wildcard); CORS preflights |
| B. Single origin | `app.<domain>` with `/api/*` reverse-proxied to the API | No CORS; cookie can be first-party | Proxy routing rules; CSP/headers from two apps on one origin; API path prefix must not collide with frontend routes |
| C. Unrelated domains | e.g. `clinic-app.com` + `clinic-api.net` | — | **Breaks the refresh cookie** (`SameSite=Strict`); would require weakening to `SameSite=None` — **not acceptable** |

No other subdomain under the same registrable domain should host untrusted or user-generated content (same-site
subdomains can bypass `SameSite`).

---

## 3. Security assessment

Severity is for the first production release. "BLOCKER" means production must not go live until it is resolved or
explicitly accepted by the owner in writing.

### 3.1 Findings summary

| ID | Severity | Area | Finding |
|---|---|---|---|
| P3-SEC-01 | **BLOCKER** | Email | No production email provider; the API refuses to start outside Development/Testing (by design). Invitations, verification and password reset depend on it |
| P3-SEC-02 | **BLOCKER** | Hosting | Data residency and hosting location not decided; health data is sensitive personal data under PDPL ([§4](#4-saudi--privacy-considerations)) |
| P3-SEC-03 | **BLOCKER** | Operations | No backup/restore capability, no tested restore, no incident-response/breach-notification runbook ([§8](#8-backup--disaster-recovery-strategy), [§4.8](#48-breach-and-security-incident-response)) |
| P3-SEC-04 | **HIGH → code done, not deployed** | Platform admin | No MFA for platform super admins (D14 deferred). A platform admin can change any clinic's status, plan and subscription. **Implemented after owner approval of two new tables ([§19.5](#195-platform-admin-mfa-p3-d5--implemented-not-deployed)); stays a go-live blocker until the key-ring prerequisite, the break-glass procedure and alerting exist.** |
| P3-SEC-05 | **HIGH** | Client IP / proxy | Forwarded headers trust a fixed IP list only (`KnownProxies`); managed load balancers usually have changing IPs, which needs `KnownNetworks`. Misconfigured → every client shares the proxy's IP (shared rate-limit bucket, wrong audit IP) or, if trust is too broad, spoofed `X-Forwarded-For` bypasses per-IP limits |
| P3-SEC-06 | **HIGH** | Logs | One-time tokens travel in URL query strings to the web app (`/reset-password?token=…`, `/verify-email?token=…`, invitation links). Reverse-proxy/load-balancer/web access logs record full URLs by default |
| P3-SEC-07 | **MEDIUM** | Sessions | GD-03: an access token stays valid ≤ 15 min after logout, password change/reset or session revoke; platform tokens are never re-checked per request ([§3.2](#32-gd-03--access-token-outlives-the-session)). **Fixed in Phase 3A ([§19.3](#193-session-revocation-p3-d3-p3-d4-gd-03-gd-15)).** |
| P3-SEC-08 | **MEDIUM** | Sessions | Under XSS, script on the web origin can call `/auth/refresh` with `X-Client-Type: mobile` (an allowed CORS header) and receive a fresh **refresh token in the body**, defeating the httpOnly cookie. Bounded by reuse detection when the real tab next refreshes, but survives if the victim closes the browser. Mitigated today only by CSP. **Fixed in Phase 3A ([§19.4](#194-xss--refresh-token-exposure-p3-sec-08)).** |
| P3-SEC-09 | **MEDIUM** | Rate limiting | Limits are per instance; N instances give N× the budget ([§3.4](#34-distributed-rate-limiting)) |
| P3-SEC-10 | **MEDIUM** | Availability | Email is sent synchronously after commit; a provider outage turns registration/invitation/reset into 500s after the data was written (e.g. clinic created, response failed) |
| P3-SEC-11 | **MEDIUM** | Accounts | Targeted lockout: anyone who knows an email can lock that account for 15 min (5 attempts); combined with `ACCOUNT_LOCKED` it also confirms the account exists (QA-06). `EMAIL_TAKEN` on registration enumerates (QA-13). Decisions still pending |
| P3-SEC-12 | **MEDIUM** | Keys | Single HS256 signing key, no rotation path (one key validates). Rotating it signs everyone out; a leaked key cannot be rotated gracefully |
| P3-SEC-13 | **MEDIUM** | Data lifecycle | No cleanup of expired refresh tokens/verification codes; no audit-log retention policy; refresh rows keep IP + user agent indefinitely |
| P3-SEC-14 | **LOW** | Sessions | GD-15: reactivating a suspended clinic re-enables access tokens issued < 15 min before suspension ([§3.3](#33-gd-15--reactivation-revives-old-access-tokens)). **Fixed in Phase 3A ([§19.3](#193-session-revocation-p3-d3-p3-d4-gd-03-gd-15)).** |
| P3-SEC-15 | **LOW** | Errors | Lock timeout (`TimeoutException` from `AcquireLockAsync`) and transient `SqlException`s map to a generic 500, not a retryable 503 |
| P3-SEC-16 | **LOW** | Host | `AllowedHosts: "*"`; production should pin the API host name |
| P3-SEC-17 | **LOW** | Bootstrap | `Bootstrap:SuperAdminPassword` is read at startup; must be supplied once from the secret store and removed afterwards (documented, not enforced) **Phase 3C: each start with the secrets still set after the account exists logs warning event 4104 (never the value); removal stays an operator step ([§21](#21-phase-3c--security-operations-and-readiness-2026-10-08)).** |
| P3-SEC-18 | **LOW** | DB grants | `least-privilege.sql` uses server-level `CREATE LOGIN` (not valid on some managed SQL offerings, which need contained users); `clinic_app` has `db_datawriter`, so hard `DELETE` of clinical rows is possible at the DB level although the app only soft-deletes **[DB-APPROVAL]** |
| P3-SEC-19 | **LOW** | Invitations | GD-04: a pending invitation can be accepted while the clinic is suspended (no access results) **Fixed in Phase 3C: refused with 403 `TENANT_SUSPENDED` before anything changes; the link works after reactivation ([§21](#21-phase-3c--security-operations-and-readiness-2026-10-08)).** |
| P3-SEC-20 | **LOW** | Platform | No platform-admin UI; operations use the platform API directly (raises operator-error risk; no step-up confirmation) |
| P3-SEC-21 | **INFO** | CSP | `style-src 'unsafe-inline'` (React style attributes); acceptable, documented |
| P3-SEC-22 | **INFO** | A11y | GD-14 native dialog → Tab reaches browser chrome **Closed in Phase 3C as accepted platform behaviour (no keyboard trap, page content stays inert) — no code change ([§21](#21-phase-3c--security-operations-and-readiness-2026-10-08)).** |
| P3-SEC-23 | **INFO** | Deps | Dev-only `braces` advisory via `eslint-config-next`; production dependencies 0 |
| P3-SEC-24 | **INFO** | Health | `/health/*` anonymous; fine, but `/health/ready` should be reachable only from the load balancer/monitoring |
| P3-SEC-25 | **INFO** | Multi-tab | Each tab refreshes independently with the same cookie; with rotation + reuse detection, two tabs refreshing at the same moment could end the session. Not reported in testing; verify in staging (Phase 3J) before deciding anything **Reproduced and fixed in Phase 3C: tabs take turns refreshing (Web Locks); browser regression test ([§21](#21-phase-3c--security-operations-and-readiness-2026-10-08)).** |

### 3.2 GD-03 — access token outlives the session

> **Phase 3A:** Option D was chosen and implemented — see [§19.3](#193-session-revocation-p3-d3-p3-d4-gd-03-gd-15). The
> analysis below is kept as written.

**Current behaviour.** Logout, password change, password reset, session revoke and tenant switch revoke refresh
tokens; the access token already issued keeps working until `exp` (≤ 15 min + 30 s skew). For clinic tokens the
membership is re-checked per request (GD-02), so disabled members are cut off immediately; for **platform** tokens
nothing is re-checked.

| Option | How | Pros | Cons |
|---|---|---|---|
| A. Keep | Accept ≤ 15 min | No work, no extra load | "Sign out everywhere" and "I changed my password because it leaked" don't take effect for up to 15 min — weak for a health product; platform admins included |
| B. Revocation / versioning | Add a `SecurityStamp`/`TokenVersion` on `User` (and/or a denylist of `jti`) and check it per request | Precise | **Schema change [DB-APPROVAL]**; per-request lookup anyway; denylist needs a shared store with >1 instance |
| C. Shorter lifetime | e.g. 5 min | One config value | Shrinks but does not close the window; 3× refresh traffic (refresh limit 120/min/IP is shared by a clinic behind one IP) |
| **D. Server-side session validation** (recommended) | Per request, check that the token's `sid` (refresh-token family) still has a live, unrevoked token, and that the user is active — one indexed query (`IX_RefreshTokens_FamilyId` exists), merged with the existing membership query for clinic tokens | Closes GD-03 **and** GD-15 for every path that already revokes families (logout, password change/reset, session revoke, suspension, reuse detection); covers platform tokens; **no schema change**; reuses existing data | One extra indexed lookup per request (today: 2 → 2–3 small queries; can be merged into 1); makes the DB a hard dependency for every request (already true for clinic requests) |

**Recommendation: D**, implemented in the API middleware next to the GD-02 check, applied to clinic **and**
platform tokens. Optional later: a few-seconds in-process cache per `sid` if load measurements require it (this
re-opens a window of that many seconds, which is the explicit trade-off). Tests: logout/password change/session revoke
→ the old access token gets 401 immediately; refresh rotation keeps the same `sid` working; reuse detection kills the
access token too; platform session revoked → 401. **[DECISION]**

### 3.3 GD-15 — reactivation revives old access tokens

> **Phase 3A:** closed by the same change (suspension revoked the families; the old access tokens now fail session
> validation) — see [§19.3](#193-session-revocation-p3-d3-p3-d4-gd-03-gd-15).

**Analysis.** Suspension revokes every refresh token of the clinic, and the middleware refuses every clinic request
while the clinic is suspended. After reactivation, the only credentials that work again are access tokens issued
before suspension and not yet expired — so the exposure is "suspended and reactivated within ≤ 15 min, by someone who
kept a token". Low impact, but suspension is the platform's emergency brake (non-payment, abuse, compromise), and a
brake that releases old credentials is surprising.

**Recommendation:** accept as LOW for go-live **only if** Option D of GD-03 is not taken; with Option D it is closed
for free (the families were revoked at suspension, so the old access tokens fail `sid` validation). No separate work
item. **[DECISION]** (bundled with GD-03)

### 3.4 Distributed rate limiting

**Today:** `System.Threading.RateLimiting` partitions in process memory. With N instances behind a load balancer the
effective budget is up to N× (round-robin) and resets on deploy. Account lockout is already cross-instance (database).

| Approach | Notes |
|---|---|
| 1. Keep per-instance, divide the configured limits by N | Zero infrastructure; budgets drift when N changes or traffic is uneven; acceptable for N ≤ 2 if documented |
| 2. Edge rate limiting (WAF/load balancer) for anonymous endpoints | Strong for login/forgot-password floods by IP; can't see user id; provider feature |
| **3. Shared store (Redis-compatible) with fixed-window/token-bucket counters** (recommended once N ≥ 2) | Exact, cross-instance; adds one managed component; needs a defined failure mode when the store is down — see below |
| 4. Database-backed counters | No new component; adds write load to SQL Server on hot paths (login, refresh); needs a table **[DB-APPROVAL]** |

**Recommended design:** keep the ASP.NET Core rate-limiter middleware and policy names; swap the partition limiter
for a distributed implementation behind the same `AddApiRateLimiting` extension (one place). Keys stay the same
(`auth`/`refresh`/`sensitive` per client IP, global per user id or IP). Failure mode: if the store is unreachable,
fall back to the in-process limiter (degraded but still limited) and raise an alert — never fail completely open on
`auth`/`sensitive`, never block all traffic. Pair with edge rules for anonymous floods. Choice of store/provider is a
**[DECISION]**; first release may run approach 1 with a single API instance or N = 2 and halved limits.

Prerequisite in all cases: correct client IP (P3-SEC-05 — add `KnownNetworks` support and set it per environment).

### 3.5 Other areas reviewed

| Area | Assessment |
|---|---|
| Password reset / invitation tokens | 256-bit, hashed, single use, short-lived, newest-only, all sessions revoked on reset — good. Fix log exposure (P3-SEC-06): strip query strings in proxy/web access logs, or move tokens into the URL fragment (code change, frontend + email templates) |
| CSRF | Refresh/logout are cookie-authenticated; protected by `SameSite=Strict`, exact-origin CORS and a JSON body (preflight). Other endpoints use bearer tokens (not ambient). Keep option A/B of §2.3; never relax `SameSite` |
| CORS | Exact origins with credentials; production must list only the production web origin; no wildcard |
| Cookies | `__Host-` prefix is not possible with `Path=/api/v1/auth`; acceptable. Cookie needs HTTPS end to end (`Secure`) |
| HTTPS | HSTS + HTTPS redirection in the API outside Development, HSTS in the Next.js production build. TLS terminates at the edge; the edge must forward `X-Forwarded-Proto` from a trusted network, otherwise `UseHttpsRedirection` loops. Consider HSTS preload only after the domain is final |
| Secrets | None committed (gitleaks clean); JWT key and connection strings via environment/secret store; Development uses an ephemeral key. Production needs a secret store and rotation procedure ([§6](#6-environment-strategy)) |
| Encryption | In transit: TLS at the edge; **API → SQL Server must use `Encrypt=True` with a trusted certificate (no `TrustServerCertificate=True`)**. At rest: TDE or provider-managed storage encryption; backups encrypted. Column-level encryption for national ID is a candidate only if legal review requires it **[LEGAL]** |
| Tenant / branch isolation | Strong (filters, save guard, stored-TenantId predicate, composite FKs, cross-tenant suites). Production adds no new path; re-run the isolation suites in staging against the production-like DB |
| Authorization | Server-side on every endpoint; fallback policy authenticated. No change required for production |
| Audit logs | Present for auth, membership, clinical and SaaS events; append-only by grant. Needs retention, export for investigations, and access restricted to owner/manager (already) and platform operators (process) |
| Error handling | Generic 500 with correlation id; details in logs only. Add 503 mapping for transient DB errors and lock timeouts (P3-SEC-15) |
| Dependencies | 0 vulnerable NuGet, 0 production npm advisories. Add scheduled scanning (new advisories appear without code changes) and container image scanning ([§9](#9-cicd-strategy)) |

---

## 4. Saudi / privacy considerations

> **Not legal advice and not a compliance claim.** This section lists questions and engineering implications. A
> Saudi legal/compliance specialist must review PDPL, its Implementing Regulations and the Regulation on Personal
> Data Transfer outside the Kingdom, as well as any health-sector requirements (MOH, NHIC/health information
> exchange policies, SCFHS for practitioners), NCA controls and CST cloud rules before go-live. Public summaries
> consulted while writing this section are listed at the end; they were not verified against the official texts.

### 4.1 Data inventory (what the system stores)

| Category | Examples in the schema | Sensitivity |
|---|---|---|
| Staff identity | name, email, mobile, password hash, IP/user agent of sessions | Personal data |
| Patient identity | full name (ar/en), Saudi ID/Iqama number, mobile, date of birth, gender, MRN | Personal data; national ID is high-risk identifier |
| Clinical | visits (complaint, examination, diagnosis + ICD-10, plan, vitals, follow-up), prescriptions | **Health data — sensitive personal data** under PDPL |
| Attachments (API only, quarantined) | patient documents | Health data (sensitive) |
| Operational | appointments, queue tokens, audit logs | Personal data; audit logs contain IPs and user ids |
| Commercial | plan, subscription status, billing period | Business data of the clinic |

### 4.2 Roles **[LEGAL]**

The clinic is most likely the **controller** of patient data; the platform operator most likely a **processor** for
patient data and a controller for clinic-account/staff data. The specialist must confirm this split, and the
contract (terms of service + data processing agreement with each clinic) must reflect it.

### 4.3 Data residency

| Item | Type |
|---|---|
| PDPL's Regulations treat storage/processing inside the Kingdom as the baseline and regulate transfers outside it (approval or listed exceptions). Default planning assumption: **all production data — database, backups, logs, attachments — stays in-Kingdom** until the specialist confirms otherwise | **[LEGAL]** + **[DECISION]** |
| Choose a hosting provider/region physically in Saudi Arabia, and confirm its status under CST cloud regulations and NCA controls relevant to a private-sector health SaaS | **[LEGAL]** + **[DECISION]** |
| Sub-processors that receive personal data: email provider (recipient email, name, one-time links), log/monitoring service (user ids, IPs, possibly names in errors), backup storage, malware scanner (file contents). Each is a potential transfer if hosted outside the Kingdom | **[LEGAL]** + **[DECISION]** |
| Engineering: one region; no cross-region replication outside the Kingdom; log pipeline and alerting configured in-region; keep PHI out of logs so the log store carries lower risk | **[TECH]** |

### 4.4 Health / medical data handling

- **[TECH]** Already: clinical content is permission-gated (`MedicalRecords.View`), tenant/branch/own-doctor scoped, not
  written to logs, not exposed to the platform plane, not shown to reception (follow-ups carry operational fields only).
- **[TECH]** Production adds: encryption in transit to the database, encryption at rest (TDE/storage + encrypted
  backups), no PHI in logs/traces/alerts, no production data in non-production environments.
- **[LEGAL]** Consent: whether the clinic needs explicit (written) patient consent for processing health data in a
  cloud system, and how that is evidenced. The product does not record consent today.
- **[LEGAL]** Medical-record retention periods (health-sector rules may set minimums) vs. PDPL's "no longer than
  necessary".
- **[LEGAL]** Whether MOH/NHIC requirements (e.g. health information exchange, Wasfaty for e-prescriptions) apply to
  a clinic using printed prescriptions from this system — currently deferred (D15).

### 4.5 Access control and auditability

- **[TECH]** In place: RBAC with clinic permissions, branch restriction, own-doctor scoping, immediate effect of
  disabling members, append-only audit log with correlation id.
- **[TECH]** Phase 3: GD-03 closure (session validation), platform-admin MFA (D14) **[DECISION]**, platform actions
  audited (already) and reviewable.
- **[OPS]** Least-privilege operator access: named accounts for people with production DB access, access reviews,
  break-glass procedure, no shared accounts; every production DB query by a person is logged.
- **[LEGAL]** Whether patient-record **read** access must be audited (today: writes and security events are audited;
  reads of clinical records are not). This affects log volume and possibly schema **[DB-APPROVAL]**.

### 4.6 Retention and deletion / deactivation

| Data | Today | Proposal | Type |
|---|---|---|---|
| Patients | Soft delete; delete blocked if any clinical history (D6) | Keep; retention period per legal review; proper deactivation needs a schema change (D6 follow-up) | **[LEGAL]** **[DB-APPROVAL]** |
| Visits / prescriptions | Soft delete; immutable after completion | Retain per medical-record rules | **[LEGAL]** |
| Audit logs | Kept forever | Define retention (e.g. N years) and archival; purge only via migrator role | **[DECISION]** **[LEGAL]** |
| Refresh tokens / verification codes | Kept forever (with IP, UA) | Scheduled cleanup of rows expired/revoked > 30 days (data deletion — **needs approval**) | **[DB-APPROVAL]** |
| Closed clinic (tenant offboarding) | No process | Export for the clinic, then retention hold, then deletion/anonymisation — needs a design | **[LEGAL]** **[DECISION]** |
| Backups | n/a | Retention window = backup policy ([§8](#8-backup--disaster-recovery-strategy)); deleted data persists in backups until they expire — document this | **[LEGAL]** |
| Data-subject requests (access/correction/deletion) | No tooling | Process via the clinic (controller); platform supports with an export procedure | **[OPS]** **[LEGAL]** |

### 4.7 Encryption

**[TECH]** TLS 1.2+ everywhere (edge, API→SQL with certificate validation, API→email provider); encryption at rest
for database, backups, logs and (later) object storage; keys in the provider's key service; JWT signing key and
connection secrets in the secret store. **[LEGAL]** Whether national ID / clinical columns need application- or
column-level encryption beyond at-rest encryption.

### 4.8 Breach and security incident response

- **[LEGAL]** PDPL Regulations require notifying the regulator (SDAIA) of a personal-data breach promptly and within
  72 hours in the cases they define, and in some cases the data subjects. Confirm who notifies (controller = clinic?
  processor = platform → controller?) and the contractual timelines between platform and clinic.
- **[OPS]** Incident-response runbook: detection sources (alerts in [§10](#10-observability)), severity levels, who
  is on call, evidence preservation (logs, audit log export), containment levers (suspend clinic, revoke sessions,
  rotate JWT key, rotate DB credentials), communication templates (ar/en), post-incident review.
- **[TECH]** Levers that must exist before go-live: suspend clinic (exists), end all sessions of a user (exists),
  **end all sessions platform-wide** (key rotation — needs P3-SEC-12 work), rotate DB credentials without downtime.

### 4.9 Vendor / data-processing considerations

**[LEGAL]** + **[DECISION]** For every vendor (hosting, email, logging/monitoring, backups, scanner, DNS/CDN): data
location, sub-processors, DPA availability, security certifications, breach-notice terms, exit/portability. Keep a
written sub-processor register. Prefer vendors that can keep data in-Kingdom.

### 4.10 Other Saudi items (not Phase 3 work, listed for completeness)

ZATCA e-invoicing (when invoicing ships), VAT, Saudi SMS sender registration (CST) and WhatsApp (D15), Nafath/NPHIES/
Wasfaty (D15), Arabic-first notices/terms. None are in Phase 3 unless approved.

Public summaries consulted (secondary sources, not verified against official texts):
[Clyde & Co — PDPL Implementing Regulations](https://www.clydeco.com/en/insights/2023/09/saudi-arabia-issues-implementing-regulations),
[PwC — KSA PDPL series](https://pwc.com/m1/en/blogs/pdf/ksa-personal-data-protection-law-series-part-1.pdf),
[Simmons & Simmons — CST Cloud Computing Regulatory Framework](https://www.simmons-simmons.com/en/publications/ckoa7fjnm7xwt0985ocg0u0jy/cloud-computing-regulatory-framework-ccrf-in-saudi-arabia).

---

## 5. Infrastructure requirements

| Component | Requirement | Notes |
|---|---|---|
| Compute — API | Linux containers or app service, .NET 10 runtime, ≥ 1 vCPU / 1–2 GB per instance to start; health probes on `/health/live` (liveness) and `/health/ready` (readiness) | Stateless; `ASPNETCORE_ENVIRONMENT=Production` |
| Compute — web | Node.js LTS, `next start`, ≥ 1 vCPU / 1 GB to start | Needs a server (per-request CSP nonce) |
| Database | SQL Server 2022-compatible (CI already uses 2022); private endpoint; `Encrypt=True`; TDE/at-rest encryption; automated full/diff/log backups or PITR; separate `clinic_app` and `clinic_migrator` principals | See [§7](#7-database-strategy) |
| Network | Private subnet for API ↔ DB; DB not internet-reachable; egress allowlist for email provider (and later scanner) | |
| Edge | TLS certificates with auto-renewal; HTTP→HTTPS; `X-Forwarded-For/Proto`; request body limit ≥ API limits; access logs without query strings | P3-SEC-06 |
| DNS | Authoritative DNS for `app.` / `api.` (same registrable domain), CAA, SPF/DKIM/DMARC for the email domain | |
| Secrets | Secret store reachable by the API/web runtime identity; no secrets in images or repo | |
| Logs | Central structured log store in-region, retention policy, role-based access | |
| Monitoring | Uptime probe (external), metrics, alert routing | |
| Backups | Backup storage separated from the primary (different account/subscription or immutable), encrypted | |
| CI/CD | Build runners, container registry (private, scanned), deployment credentials scoped per environment | |

---

## 6. Environment strategy

```text
Development (local) → Testing/QA (CI, ephemeral) → Staging (production-like) → Production
```

| | Development | Testing / QA | Staging | Production |
|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Testing` (integration tests) | `Staging` | `Production` |
| Database | LocalDB (`ClinicSaaS_Dev*`), auto-migrate | Throw-away `ClinicSaaS_Test_<guid>` per run (CI container) | Dedicated server, same version/edition class as production; migrations applied by the pipeline with the migrator principal | Dedicated server; migrations by pipeline only after staging success; `MigrateOnStartup` refused (exists) |
| Configuration | `appsettings.Development.json` | Test factory overrides | `appsettings.Staging.json` (**to add**) + environment variables | `appsettings.Production.json` (**to add**) + environment variables |
| Secrets | Ephemeral JWT key; no real secrets | Generated per run, masked | Staging secret store (separate from production) | Production secret store; access audited; rotation schedule |
| Email | DevMailbox (files) | Replaced by test double | Real provider in **sandbox/test mode** or a restricted allowlist of internal recipients | Real provider, verified domain |
| Logging | Console, text | Console | Structured, central, short retention | Structured, central, retention per policy, PHI-safe |
| External integrations | None (no real calls) | None | Sandbox endpoints only | Production endpoints |
| Test data | Synthetic only | Synthetic, created per test | **Synthetic only** — never a copy of production | Real data |
| Access | Developer | CI | Team + owner; behind authentication/IP allowlist (not publicly indexable) | Public app; admin/DB access restricted to named operators with MFA |
| Rate limits | Defaults (raised only for local e2e) | Test overrides | Production values | Production values |
| Bootstrap admin | Optional | Tests | Once, then removed | Once, then removed (P3-SEC-17) |

Rules:

- Production must never start with Development settings: already enforced for DevMailbox, missing email provider,
  local file storage, `MigrateOnStartup`, and empty JWT key. **To add** (Phase 3B): refuse `TrustServerCertificate=True`,
  `localhost` CORS origins, `http://` `Auth:AppBaseUrl`, empty `ForwardedHeaders` trust, `AllowedHosts=*` in
  Production.
- No production data in Staging or below. If a production defect needs data, reproduce it with synthetic data.
- Each environment has its own JWT signing key, DB credentials and email credentials.

---

## 7. Database strategy

Read-only assessment; **no schema change is proposed for implementation here**. Every index/schema idea below is a
future change that needs explicit approval.

### 7.1 Indexes and query risks

| Area | Observation | Risk | Future change? |
|---|---|---|---|
| Tenant-leading indexes | Every operational index starts with `TenantId` (schema rule) | Good for the tenant filter | — |
| Appointments | `IX_Appointments_Doctor_Start` (INCLUDE EndUtc, Status, Source), `UX_Appointments_ActiveSlot` (filtered unique), `IX_Appointments_Tenant_Start`, `(TenantId, PatientId, StartUtc)`, `(TenantId, BranchId, StartUtc)` | Overlap check, board and availability are covered | — |
| Patients search | Prefix (`StartsWith`) on name, exact on mobile/ID, prefix on MRN — all indexed | Arabic "contains" search not supported; full-text later | Full-text index **[DB-APPROVAL]** (phase 12 idea) |
| Audit logs | `(TenantId, OccurredAtUtc)`, `(TenantId, EntityType, EntityId)`, `(UserId, OccurredAtUtc)` | Grows fastest; failed-login rows with no tenant | Partitioning/archival later **[DB-APPROVAL]** |
| Refresh tokens | `TokenHash` unique, `FamilyId`, `(UserId, RevokedAtUtc)`, `ExpiresAtUtc` | Grows without cleanup; `FamilyId` index supports GD-03 option D | Cleanup job (data deletion, approval) |
| Per-request checks | Membership by PK + tenant access (tenant PK + subscription filtered index) | 2 small queries per clinic request | Merge into one query (code only) |
| Plan/feature lookups | `CurrentPlanResolver` per request where used | Small | Cache per request (exists) / short cache later |

### 7.2 Concurrency and transaction boundaries

- Booking/reschedule: exclusive `sp_getapplock` per doctor (both doctors on a doctor change), ReadCommitted, overlap
  query, insert/update, commit; unique filtered index as backstop mapped to `SLOT_TAKEN`. Walk-in tokens: lock per
  clinic-day. Visit completion vs prescription: lock per visit. These are correct and tested (concurrency + mutation
  tests) and **must not change** in Phase 3.
- Lock timeout 10 s; command timeout 30 s. Under load, a 10 s wait on one doctor's lock is a symptom to alert on.
- `sp_getapplock` requires the application principal to have `EXECUTE` on it (granted via `GRANT EXECUTE`); verify
  on the chosen SQL offering.

### 7.3 Transient failures and retry strategy

The code deliberately has **no** `EnableRetryOnFailure`. Turning it on naively is unsafe:

1. EF's retrying execution strategy refuses user-initiated transactions (`InTransactionAsync`) unless wrapped in
   `CreateExecutionStrategy().ExecuteAsync(...)`, which re-runs the **whole** unit of work.
2. Re-running is not safe as written: entities are created and tracked before the transaction starts (e.g. the
   `Appointment` in `AppointmentService.CreateAsync`), audit entries are added to the change tracker, and emails are
   sent after commit. A retry could duplicate tracked entities or emails.
3. **Commit-unknown:** if the connection drops after the server committed but before the client saw it, a retry
   repeats a booking (the unique index then reports `SLOT_TAKEN` for the user's own booking), a patient registration
   (new MRN) or a prescription (new number).

**Safe strategy (proposed, Phase 3E):**

| Layer | Policy |
|---|---|
| Connection establishment | Enable SqlClient connect retry (`ConnectRetryCount`/`ConnectRetryInterval` in the connection string) — safe, nothing has executed |
| Reads (GET, no transaction) | Retry known transient SQL error numbers a small number of times with jittered back-off, via a narrowly scoped helper or an execution strategy used only for queries |
| Writes with explicit transactions (booking, reschedule, walk-in, patient create, prescription, visit completion, registration, invitations) | **No automatic retry.** Map transient errors and lock timeouts to **503 `TEMPORARILY_UNAVAILABLE` + `Retry-After`** (new error code — API contract addition, documented); the user retries consciously; the slot guard and unique indexes keep a repeated submit safe |
| Writes without explicit transactions | Same as above for the first release (simplicity, one rule) |
| Future | Idempotency keys for `POST /appointments` and other creates (client sends `Idempotency-Key`; server stores result) — needs a table **[DB-APPROVAL]**; only then can writes be retried automatically |

Tests: fault injection in integration tests (simulated transient error before commit → 503, nothing written;
lock timeout → 503); existing concurrency/mutation tests must stay green.

### 7.4 Connection pooling

Default ADO.NET pooling (max 100 per connection string per process). Size the SQL tier's connection limit to
`instances × pool max` with headroom. `AddDbContextPool` is **not** recommended: `AppDbContext` takes request-scoped
`ITenantContext`/`ICurrentUser` in its constructor (tenant filter), which pooling would share across requests.

### 7.5 Migration strategy

- Production applies the reviewed idempotent scripts (`docs/database/NNN_*.sql`) with the `clinic_migrator`
  principal, as a separate pipeline step **before** the new application version is deployed (exists as a rule;
  automate in Phase 3G).
- Expand → migrate → contract: a migration must be compatible with the currently running app version (add nullable
  columns/new tables first; drop/rename only after the old version is gone). Destructive steps need explicit approval.
- No down-migrations in production; rollback = forward fix, or restore as last resort ([§8](#8-backup--disaster-recovery-strategy)).
- Before every production migration: verified backup/PITR point, staging run on a production-sized synthetic
  dataset, timing measured, lock impact reviewed.
- EF `has-pending-model-changes` stays in CI.

### 7.6 SQL Server version considerations

- CI uses SQL Server 2022; developers use LocalDB 17. Production should be 2022-compatible (managed or
  self-managed). Verify the compatibility level EF Core 10 targets and pin it explicitly (`UseCompatibilityLevel`) to
  the production engine — **[DECISION]** once the SQL offering is chosen (code change, no schema change).
- Features used: filtered indexes, `rowversion`, `sp_getapplock`, `OUTPUT` clauses, composite FKs, check
  constraints — available in all current editions and in the common managed offerings; confirm on the chosen one.
- Least-privilege script must be adapted for managed offerings that use contained database users (P3-SEC-18).

---

## 8. Backup & disaster recovery strategy

**Currently achieved: nothing in production** (no production exists). Locally there are no scheduled backups.
The targets below are **proposals** for the owner to accept or change **[DECISION]**.

| Item | Proposal |
|---|---|
| SQL backups | Full weekly, differential daily, transaction log every 5–15 min — or a managed service's automated backups with point-in-time restore |
| Proposed RPO | ≤ 15 minutes (requires log backups/PITR) |
| Proposed RTO | ≤ 4 hours for a full region-level rebuild in the same region; ≤ 1 hour for a database PITR restore |
| Retention | Operational PITR window 7–35 days; monthly long-term copies for N months/years per legal retention review **[LEGAL]** |
| Storage | Encrypted, in-Kingdom, separated from the primary (different account/subscription or immutable/WORM) so a compromised production credential can't delete backups |
| Restore testing | Monthly automated restore into an isolated environment + integrity check (`DBCC CHECKDB` where available) + row-count/smoke queries; quarterly full DR drill timed against RTO; results recorded |
| Application recovery | Immutable versioned images in the registry; infrastructure as code; redeploy any previous version |
| File recovery (attachments, later) | Object versioning + soft delete; same retention and residency as the DB; DB rows and objects restored to a consistent point (restore DB, then reconcile orphans) |
| Secrets recovery | Secret store with soft delete/versioning; documented break-glass access to recreate JWT key and DB credentials; JWT key loss = everyone signs in again (acceptable) |
| DR scope | First release: single region, multi-zone if available; cross-region DR only if an in-Kingdom second region exists and the owner accepts the cost |

---

## 9. CI/CD strategy

Reuse `.github/workflows/ci.yml` as the verification stage; add build/deploy stages in a separate workflow.

```text
PR / push ─► CI (existing): backend build+unit+integration, EF model check, NuGet vuln,
             frontend lint/typecheck/vitest/build/npm audit, gitleaks
         ─► NEW: browser suite (API + web + SQL Server in CI), dependency review on PRs
merge to integration ─► NEW: build artifacts
             ├─ API container image (dotnet publish, non-root, read-only FS)
             ├─ web container image (next build, standalone output) — per environment while NEXT_PUBLIC_API_URL is build-time
             ├─ migration bundle: idempotent SQL script for the target version
             ├─ SBOM + image vulnerability scan (fail on high/critical with fix available)
             └─ sign/tag images with the commit SHA
         ─► deploy Staging (manual approval or automatic)
             ├─ backup checkpoint → apply migration script (migrator principal) → deploy API → deploy web
             ├─ smoke tests (health, login, booking happy path with synthetic clinic)
             └─ browser suite against Staging (synthetic data)
         ─► deploy Production (manual approval by owner, same artifacts, no rebuild)
             ├─ backup/PITR checkpoint → migration → rolling deploy (≥2 instances) → smoke tests
             └─ automatic rollback of the app on failed health/smoke; DB = forward fix
```

| Topic | Plan |
|---|---|
| Build | Reuse CI jobs; Release configuration; pinned SDK/Node versions |
| Tests | Unit + integration (existing), browser suite in CI (new; P2 item from the MVP gate), smoke tests per environment |
| Security scanning | SAST: .NET analyzers + optional CodeQL; secret scanning (gitleaks, existing); dependency scanning (NuGet vuln, `npm audit`, scheduled weekly as well as on PR); container scanning |
| Artifacts | Immutable images by SHA; migration script attached to the release |
| Database migrations | Separate step with migrator credentials; idempotent; never from app startup in Production (enforced) |
| Rollback | App: redeploy previous image (expand/contract keeps the schema compatible). DB: forward fix; restore only for data corruption |
| Promotion | Same artifact Staging → Production; production deploy needs a manual approval; no direct pushes to production |
| Runtime config for web | Option: serve `NEXT_PUBLIC_API_URL` at runtime (small code change) to keep one image for all environments **[DECISION]** |
| Credentials | Deployment uses short-lived, environment-scoped identities (OIDC from CI to the cloud) — no long-lived cloud keys in CI secrets |

---

## 10. Observability

### 10.1 Structured logging

| Item | Plan |
|---|---|
| Format | JSON (Serilog compact/JSON formatter) to stdout; collected by the platform's log agent; keep the text template for Development |
| Standard fields | timestamp, level, message template, `CorrelationId`, `TraceId`/`SpanId` (OpenTelemetry), `UserId`, `TenantId`, environment, version (commit SHA), instance |
| Request log | method, route template (not raw path when possible), status, duration, `CorrelationId`; **no query strings, no bodies, no headers** (Authorization/cookies never) |
| Security events (log + audit) | login success/failure/lockout, refresh-token reuse, session revoke, password change/reset, invitation accept, tenant switch, permission denied (403), cross-tenant violation (Critical), rate-limit rejection (sampled), suspension/reactivation, platform actions |
| Business events | appointment created/rescheduled/cancelled/no-show, visit completed, prescription issued — **ids only**, never clinical text |
| Errors | exception type + stack in logs; correlation id returned to the user; no PHI in exception messages (review `DomainException` messages) |
| Never logged | passwords, access/refresh tokens, one-time link tokens, `Authorization` header, cookies, connection strings, API keys, clinical free text, national ID numbers, full mobile numbers (mask) |
| Retention | Operational logs 30–90 days in-region **[DECISION]**; audit log in DB per retention policy |
| Access | Logs are personal data: role-based access, no export outside the region |

### 10.2 Metrics and tracing (recommended)

OpenTelemetry for ASP.NET Core, HttpClient and SqlClient: request rate/latency/error rate per route, DB call
latency, lock wait time (custom metric around `AcquireLockAsync`), rate-limit rejections by policy, email send
success/failure, refresh reuse detections, 401/403 rates, GC/CPU/memory. Exporter target chosen with the provider
**[DECISION]**.

### 10.3 Monitoring and alerting

| Signal | Check | Alert (proposed thresholds — tune in staging) |
|---|---|---|
| Availability | External probe on `https://app.<domain>` and `https://api.<domain>/health/live` every 1 min | 2–3 consecutive failures → page |
| API readiness | `/health/ready` from the load balancer | Instance removed + alert if all instances unready |
| Database health | Readiness check, DB CPU/IO/storage, connection count, deadlocks, long-running queries, backup job status | Storage > 80 %, backup failure, connections near limit |
| Authentication failures | Login failure rate, lockouts, refresh reuse detections | Spike vs baseline; any reuse spike |
| Rate-limit events | 429 count by policy | Sustained spike (attack or a clinic behind one IP hitting limits) |
| Exceptions | 5xx rate, `TenantIsolationViolationException` | 5xx > 1 % for 5 min; **any** isolation violation → page |
| Latency | p95/p99 per route; booking endpoint separately | p95 > 1 s for 10 min |
| Lock waits | `sp_getapplock` wait time / timeouts | Any timeout burst |
| Email | Send failures, bounces/complaints from provider webhooks (later) | Failure rate > threshold |
| Certificates | Expiry | < 21 days |
| Background jobs (when they exist) | Last success time per job | Missed schedule |

---

## 11. Email

### 11.1 Current abstraction

`IEmailSender.SendAsync(EmailMessage)` with HTML body; Arabic RTL template built in `AuthService`/`StaffService`;
`DevMailbox` (Development only) and `NotConfiguredEmailSender`; startup guard refuses Production without a provider.
Messages today: email verification, password reset, staff invitation. Sends happen synchronously after commit.

### 11.2 Requirements

| Message | Requirement |
|---|---|
| Verification | Link to `/verify-email`, 48 h; resend rate-limited (exists) |
| Invitation | Link with seat id; must reach external staff mailboxes reliably |
| Password reset | Link 30 min single use; deliverability critical; no enumeration (always 202, exists) |
| Operational | Later: subscription ending, suspension notice, security notice ("your password was changed", "new sign-in") — recommended for production |
| All | Arabic-first with English variant per `PreferredLocale`; plain-text alternative; SPF/DKIM/DMARC aligned sending domain; no PHI in emails; links to the production `Auth:AppBaseUrl` only (HTTPS) |

### 11.3 Approaches compared (provider-agnostic)

| Approach | Pros | Cons |
|---|---|---|
| Transactional email API (HTTPS) | Best deliverability tooling, bounce/complaint webhooks, templates, sandbox mode | Vendor lock-in per adapter; data location of the vendor **[LEGAL]** |
| SMTP relay (TLS) to a provider | Generic adapter works with many providers (incl. in-Kingdom options) | Fewer delivery insights; connection handling |
| Self-hosted mail server | Full control | Deliverability and operations burden — **not recommended** |

**Recommendation:** implement **one** generic adapter first (SMTP over TLS, or the chosen provider's HTTPS API),
selected by `Email:Provider`, credentials from the secret store; provider choice by the owner considering data
location **[DECISION]**. Move sending out of the request path with a transactional **outbox** (row written in the same
transaction, a background worker sends with retries) — this is the planned `NotificationMessages` table and therefore
**[DB-APPROVAL]**. Interim option without schema change: send after commit inside a `try/catch` that logs and returns
success, plus a "resend" path (already exists: `ResendVerificationAsync`, `StaffService.ResendInvitationAsync`; a
lost reset email is simply requested again) — accepted
loss of a message vs. a 500 after data was written. **[DECISION]**

Rules: no real emails from local development (DevMailbox stays); Staging uses provider sandbox or an allowlist;
no credentials in the repo; tests use the existing test double.

---

## 12. Attachments

MVP decision **D3 stands**: attachments stay hidden and quarantined. Phase 3 may **design** production storage;
**implementation and enabling require a separate owner approval**.

### 12.1 DESIGN (no implementation in Phase 3 unless approved)

| Concern | Design |
|---|---|
| Object storage | Private bucket/container per environment, in-Kingdom; no public access policy; server-side encryption with provider-managed or customer-managed keys; versioning + soft delete |
| Keys | Keep the existing server-generated key shape `{tenant:N}/{yyyy}/{MM}/{random:N}`; tenant prefix enables per-tenant export/deletion |
| Access path | Keep **API-proxied download** (`GET /attachments/{id}/content`) for the first version: authorization, tenant/branch/doctor scoping and audit stay in one place. Signed URLs only as a later optimisation: short-lived (≤ 5 min), single object, `Content-Disposition: attachment`, issued only after the same authorization check, never cached |
| Upload | Streamed through the API (size limit exists) to a **quarantine** prefix; state `Pending` (exists) |
| Malware scanning | Asynchronous scanner (provider-native, ClamAV-based service, or vendor API **[DECISION]**/[LEGAL] for file content leaving the boundary); worker moves `Pending → Scanning → Clean/Rejected/Failed` (state machine exists); only `Clean` downloadable (exists); rejected files deleted after a hold period |
| File-type checks | Allowlist by content sniffing, not only extension/MIME (review current implementation during approval) |
| Tenant isolation | DB row is tenant-owned with composite FKs (exists); storage key prefix checked against the row's tenant on read |
| Branch isolation | Inherits the patient/visit branch scoping in `AttachmentService` (exists) |
| Retention | Same as clinical records **[LEGAL]**; deletion = soft delete in DB, object delete after retention |
| Backup/recovery | Object versioning + cross-zone redundancy; restore aligned with DB PITR |
| Abstraction | New `IFileStorage` implementation (interface exists); startup guard already refuses local disk outside Development/Testing |

### 12.2 IMPLEMENTATION (future, separately gated)

Storage adapter → scanner worker (needs background processing) → staging verification with EICAR test files →
security review → owner approval → UI enablement (separate frontend task). Not part of the first production release
unless the owner decides otherwise.

---

## 13. Performance / scaling

No load test has been run; nothing here is a measured result. Phase 3J includes a baseline load test in staging.

| Area | Analysis | Plan |
|---|---|---|
| API throughput | Each clinic request runs 2 small indexed queries before the handler (membership + tenant access); GD-03 option D adds one (mergeable). Handlers are simple EF queries with paging | Measure in staging; merge per-request checks into one query if needed |
| SQL bottlenecks | Likely first: audit-log inserts on every write, refresh-token writes, patient search by name prefix | Watch query store / top queries in staging; index changes need approval |
| Appointment concurrency | Serialized **per doctor** (not per clinic) — contention only when the same doctor is booked concurrently; walk-ins serialize per clinic-day | Metric on lock waits; keep design |
| Caching | Only the plan catalog (in-memory output cache). Per-request tenant/plan lookups are cheap | No distributed cache for the first release; revisit with measurements (a cache would also delay suspension effects) |
| Static assets | `_next/static` content-hashed; fonts self-hosted | Long cache headers at the edge; CDN optional |
| Frontend | Client-side data fetching through one client; dynamic rendering due to CSP nonce | Measure Core Web Vitals on 3G/4G mobile profiles in staging |
| Rate limiting | See §3.4 | Distributed store when ≥ 2 API instances |
| Multiple instances / horizontal scaling | API is stateless except limiter/output cache; no sticky sessions needed; DB is the shared bottleneck | Scale API horizontally; scale DB vertically first |
| Background processing | None today. Needed for: email outbox, token/code cleanup, attachment scanning, future reminders | Start as a hosted service inside the API (single-run guarded by `sp_getapplock`) or a separate worker process **[DECISION]** |

---

## 14. Cost model

Conceptual only — **no provider prices are given** because none were verified and the provider is not chosen.
Relative weight: ● small · ●● medium · ●●● large share of the monthly bill (typical for this kind of system).

| Item | Minimum viable production | Recommended | Future scaling | Weight |
|---|---|---|---|---|
| Frontend (Node) | 1 small instance | 2 small instances | Autoscale; CDN | ● |
| API | 1 small instance | 2 small instances (zero-downtime deploys) | Autoscale on CPU/latency | ●–●● |
| SQL Server | Smallest production tier with automated backups (licensing is a major factor — managed services include it; self-managed needs licences) | Zone-redundant tier with PITR | Larger tier, read replica | ●●● |
| Cache / limiter store | None (1 API instance) | Small managed Redis-compatible instance (only with ≥ 2 API instances) | Larger/HA | ● |
| Object storage | None (attachments hidden) | — | Per GB + operations + scanning | ● (later) |
| Email | Transactional plan sized to volume (verification, invites, resets — low volume) | Same + dedicated IP only if volume demands | — | ● |
| Monitoring / logs | Provider-native logs + uptime probe | Logs + metrics + alerting with retention | SIEM | ●–●● (log volume drives cost) |
| Backups | Included PITR window | + long-term retention copies | + second region | ● |
| DNS / TLS | Managed DNS; free/automated certificates | Same + WAF | — | ● (WAF ●●) |
| Engineering/ops time | Runbooks, on-call, restore drills | — | — | Often larger than infrastructure |

Main cost drivers to decide early: SQL Server licensing/tier, number of always-on instances, log retention volume,
WAF.

---

## 15. Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | Legal review concludes requirements the architecture doesn't meet (residency of sub-processors, consent, read-audit) | Medium | High | Start legal review first (Phase 3A); keep everything in-Kingdom by default |
| R2 | Refresh cookie broken by domain layout | Low (if §2.3 A/B) | High | Decide layout before infrastructure; staging test of reload/refresh |
| R3 | Enabling SQL retries breaks booking guarantees | Medium if done naively | High | §7.3 strategy; concurrency tests stay mandatory |
| R4 | Client IP wrong behind the load balancer | Medium | Medium–High | `KnownNetworks`, staging verification of audit IP and rate limiting |
| R5 | Email outage blocks onboarding/reset | Medium | Medium | Outbox or tolerant send; alerting; resend paths |
| R6 | Restore never tested → backups unusable | Medium | Critical | Mandatory restore drill before go-live and monthly |
| R7 | PHI leaks into logs/alerts | Medium | High | Logging rules §10.1, log review in staging, tests that assert no tokens/clinical text in logs |
| R8 | Single operator / no on-call | High | High | Define on-call and escalation before go-live |
| R9 | Scope creep (billing, SMS, attachments) into Phase 3 | Medium | Medium | Gated tasks; deferred items stay deferred |
| R10 | Platform admin account compromise | Low–Medium | Critical | MFA (D14), IP restriction for platform API, session validation (GD-03 D) |
| R11 | Multi-tab refresh race ends sessions (P3-SEC-25) | Unknown | Medium (UX) | Verify in staging; fix only with evidence |
| R12 | Unbounded growth of tokens/audit tables | High over time | Low–Medium | Approved cleanup job and retention policy |

---

## 16. Decisions requiring approval

None of these is chosen in this plan.

| ID | Decision | Options / recommendation | Blocks |
|---|---|---|---|
| P3-D1 | Hosting provider and region | In-Kingdom region recommended; provider-agnostic until chosen | 3H, 3I, most infra |
| P3-D2 | Data residency policy (incl. sub-processors) | All data in-Kingdom by default, pending legal review | P3-D1, P3-D4, P3-D8 |
| P3-D3 | Legal/compliance review engagement (PDPL, health sector, NCA/CST) | Required before go-live | Production gate |
| P3-D4 | Email provider + sending domain | Generic SMTP/TLS or provider API; data location | 3D |
| P3-D5 | Email delivery model | Outbox (**DB-APPROVAL**) vs tolerant synchronous send | 3D |
| P3-D6 | Token revocation strategy (GD-03, GD-15) | **Option D** recommended | 3A |
| P3-D7 | Platform-admin MFA (D14) for go-live | Recommended before go-live | 3A |
| P3-D8 | Monitoring/logging provider and log retention | In-region; 30–90 days proposed | 3C |
| P3-D9 | Distributed rate limiting | Per-instance with 1 instance, or shared store with ≥ 2 instances | 3F |
| P3-D10 | SQL Server offering/tier and compatibility level | Managed with PITR recommended | 3H |
| P3-D11 | RPO / RTO targets | Proposed RPO ≤ 15 min, RTO ≤ 4 h | 3I |
| P3-D12 | Backup retention (short- and long-term) | 7–35 days PITR + long-term per legal | 3I |
| P3-D13 | Domain layout | Sibling subdomains (§2.3 A) recommended | 3H |
| P3-D14 | Number of instances at launch | 1+1 (web+API) minimum vs 2+2 recommended | 3F, cost |
| P3-D15 | Attachments in the first production release | Stay hidden (D3) recommended | 3K |
| P3-D16 | Attachment storage + scanner provider (if P3-D15 = yes) | — | 3K |
| P3-D17 | Account enumeration (QA-06, QA-13) and lockout policy | Decide before public launch | 3A |
| P3-D18 | Data cleanup jobs (tokens/codes) and audit-log retention | Cleanup > 30 days after expiry proposed (**data deletion**) | 3J |
| P3-D19 | `RequireConfirmedEmail` in production | Recommended `true` for staff accounts | 3B |
| P3-D20 | Web runtime configuration (`NEXT_PUBLIC_API_URL`) | One image per environment vs runtime config | 3G |
| P3-D21 | Background processing host | In-API hosted service vs separate worker | 3D, 3J |
| P3-D22 | Mobile refresh-token-in-body mode (P3-SEC-08) | Disable until a native app exists, or never return it when a cookie is present | 3A |
| P3-D23 | WAF at launch | Recommended | 3H |
| P3-D24 | External penetration test before go-live | Recommended | 3L |

---

## 17. Phase 3 implementation roadmap

Each task is a separate gate: implement → verify → report → **owner approval** before the next. No task starts
without approval; no automatic merge, PR or progression. Order reflects dependencies; tasks marked "parallel-safe"
can be approved in parallel.

### Phase 3A — Owner decisions and legal review kickoff (no code)

| Field | Content |
|---|---|
| Objective | Record decisions P3-D1…D24 that unblock engineering; start the legal/compliance review |
| Affected | `docs/STATUS.md`, this plan (decisions table) |
| Dependencies | — |
| Database impact | None |
| Security impact | Sets the security posture (MFA, revocation, residency) |
| Tests | — |
| Rollback | n/a |
| Acceptance | Decisions recorded with date and owner; legal reviewer engaged; open legal questions listed |

### Phase 3B — Session and auth hardening

| Field | Content |
|---|---|
| Objective | GD-03/GD-15 per P3-D6 (session validation, clinic + platform tokens); P3-SEC-08 per P3-D22; 503 mapping for lock timeouts; enumeration/lockout changes only if P3-D17 decides; MFA only if P3-D7 decides (MFA likely needs schema → separate approval) |
| Affected | `Api/Infrastructure/TenantAccessMiddleware.cs` (or a sibling middleware), `Application/Saas/TenantAccess.cs`, `AuthController.cs`, `GlobalExceptionHandler.cs`, `ErrorCatalog.cs`, `docs/05-api-reference.md`, `docs/04-security-model.md`, frontend client only if a new error code needs UI text |
| Dependencies | 3A |
| Database impact | None for session validation (uses existing `RefreshTokens.FamilyId` index). MFA: **[DB-APPROVAL]** |
| Security impact | Closes GD-03, GD-15; removes refresh-token exfiltration path |
| Tests | Integration: old access token 401 immediately after logout/password change/reset/session revoke/suspension; refresh rotation keeps session; reuse detection kills access token; platform session revoked → 401; mobile mode behaviour; lock timeout → 503. Full backend + frontend + browser suites |
| Rollback | Revert commit (middleware-only change) |
| Acceptance | All tests green; GD-03/GD-15 marked fixed in `STATUS.md`; per-request query count measured and reported |

### Phase 3C — Configuration, secrets and environment separation

| Field | Content |
|---|---|
| Objective | `appsettings.Staging.json` / `appsettings.Production.json` (no secrets); startup guards for unsafe production settings (§6 rules); `ForwardedHeaders:KnownNetworks`; `AllowedHosts`; JWT key rotation (multiple validation keys, one signing key — P3-SEC-12); SQL `Encrypt=True` requirement; secret-store integration via environment/provider configuration source |
| Affected | `Program.cs`, `appsettings*.json`, `JwtTokenService.cs`/`JwtOptions`, `backend/README.md`, `docs/04-security-model.md` |
| Dependencies | 3A (provider for secret store can stay abstract: environment variables work everywhere) |
| Database impact | None |
| Security impact | Prevents dev settings in production; enables graceful key rotation; correct client IP |
| Tests | Integration tests for each startup guard (environment = Production with unsafe setting → refuses to start); key rotation (token signed with old key still valid during overlap, new tokens signed with new key); forwarded headers from trusted/untrusted networks |
| Rollback | Revert; configuration-only for deployed environments |
| Acceptance | Guards tested; README configuration table updated; no secret in repo (gitleaks) |

### Phase 3D — Production email (parallel-safe with 3E/3F after 3C)

| Field | Content |
|---|---|
| Objective | One real provider adapter (P3-D4) behind `IEmailSender`; delivery model per P3-D5; plain-text alternative; locale-aware templates; security notification emails optional |
| Affected | `Infrastructure/Notifications/`, `Infrastructure/DependencyInjection.cs`, `AuthService.cs`/`StaffService.cs` (only send path), README |
| Dependencies | 3A (provider, model), 3C (secrets) |
| Database impact | Outbox → **[DB-APPROVAL]** (planned `NotificationMessages`); tolerant send → none |
| Security impact | No tokens in logs; provider credentials in secret store; SPF/DKIM/DMARC |
| Tests | Adapter unit tests with a fake transport; integration tests unchanged (test double); provider failure → no 500 after commit (per chosen model); staging: real delivery to allowlisted inboxes |
| Rollback | Switch `Email:Provider`; revert adapter |
| Acceptance | Staging delivers verification/reset/invitation in ar and en; bounce handling documented; no real email from Development |

### Phase 3E — SQL resilience (parallel-safe)

| Field | Content |
|---|---|
| Objective | §7.3 strategy: connect retry, read-only retry, 503 for transient write failures; no retry around transactional writes |
| Affected | `Infrastructure/DependencyInjection.cs`, `AppDbContext.cs` (helpers only), `GlobalExceptionHandler.cs`, `ErrorCatalog.cs`, API reference, frontend error message for the new code |
| Dependencies | 3B (shares error mapping) |
| Database impact | None |
| Security impact | None directly; availability |
| Tests | Fault-injection integration tests (transient error on read → retried; on write → 503, nothing committed twice); **all appointment concurrency + mutation tests**; full suites |
| Rollback | Revert |
| Acceptance | Booking guarantees unchanged (concurrency suites green); new error code documented |

### Phase 3F — Distributed rate limiting (only if P3-D9 requires it; parallel-safe)

| Field | Content |
|---|---|
| Objective | Shared-store limiter behind `AddApiRateLimiting`, same policies and keys, in-process fallback + alert when the store is down |
| Affected | `Api/Infrastructure/RateLimiting.cs`, `Program.cs`, configuration, possibly one new package (justify per CLAUDE.md §23) |
| Dependencies | 3A (P3-D9), 3C (KnownNetworks/client IP) |
| Database impact | None (store outside SQL) — DB-backed variant would need **[DB-APPROVAL]** |
| Security impact | Consistent limits across instances |
| Tests | Integration with a local store container in CI; two API instances share a budget; store outage → fallback limiter still limits `auth`/`sensitive` |
| Rollback | Configuration switch back to in-process |
| Acceptance | Tests green; operational doc for the store |

### Phase 3G — Observability

| Field | Content |
|---|---|
| Objective | JSON logs, enrichment (version, environment), OpenTelemetry metrics/traces (P3-D8), security/business event logging per §10.1, lock-wait metric, log redaction rules, alert definitions as code where the provider allows |
| Affected | `Program.cs`, `Api/Infrastructure/Middleware.cs`, services (event log lines only), possibly OpenTelemetry packages (justify) |
| Dependencies | 3A (P3-D8), 3C |
| Database impact | None |
| Security impact | No PHI/tokens in logs (verified) |
| Tests | Tests that capture log output for login/reset/booking and assert no password/token/clinical text; correlation id present end to end |
| Rollback | Revert; logs fall back to console text |
| Acceptance | Staging dashboards + alerts firing on synthetic failures |

### Phase 3H — CI/CD

| Field | Content |
|---|---|
| Objective | Browser suite in CI; container images (API, web); SBOM + image scan; migration script artifact; staging and production deployment workflows with manual approval; OIDC deployment identity; scheduled dependency scans |
| Affected | `.github/workflows/` (new workflow; `ci.yml` extended), `Dockerfile`s (new), `frontend/next.config.ts` (`output: "standalone"` if chosen), `frontend/e2e` config for CI |
| Dependencies | 3A (P3-D1, P3-D20), 3C |
| Database impact | Pipeline applies existing scripts only; no new migrations |
| Security impact | Supply-chain controls; no long-lived cloud keys |
| Tests | Pipeline runs green on a branch; image scan gate verified with a known-vulnerable test image (in a sandbox) |
| Rollback | Workflows can be disabled; deployments use previous image tag |
| Acceptance | One full build → staging deploy → smoke → browser suite run documented |

### Phase 3I — Deployment infrastructure (staging first)

| Field | Content |
|---|---|
| Objective | Infrastructure as code for staging (then production): network, edge/TLS, web + API compute, SQL Server, secret store, log/metric sinks, DNS (P3-D13), WAF (P3-D23), least-privilege DB principals adapted to the offering |
| Affected | New `infra/` directory (IaC), `docs/database/security/least-privilege.sql` (adapted copy, reviewed) |
| Dependencies | 3A (P3-D1, D2, D10, D13, D14), 3H |
| Database impact | Creates empty databases and principals; applies migrations 001–004 via scripts — no schema change |
| Security impact | Private DB, TLS, encryption at rest, least privilege |
| Tests | Staging smoke; TLS/headers scan; DB not publicly reachable (verified); client IP correct in audit log |
| Rollback | IaC destroy/recreate for staging; production changes via reviewed plans only |
| Acceptance | Staging reachable at final-style domains; refresh across reload works; all health checks green |

### Phase 3J — Backup, restore and data lifecycle

| Field | Content |
|---|---|
| Objective | Backup configuration per P3-D11/D12; automated restore test; DR runbook; cleanup job for expired tokens/codes and audit retention per P3-D18 (if approved) |
| Affected | IaC, runbooks in `docs/operations/` (new), background job code (if approved) |
| Dependencies | 3I |
| Database impact | Cleanup job deletes expired operational rows — **explicit approval required** |
| Security impact | Recoverability; reduced stored IP/UA data |
| Tests | Restore drill into isolated environment with timing; post-restore smoke + integrity checks; cleanup job tests (deletes only expired/revoked rows older than threshold; never touches active sessions) |
| Rollback | Disable job; restore from backup |
| Acceptance | Restore achieved within proposed RTO and documented; achieved RPO measured |

### Phase 3K — Attachments production (optional; only if P3-D15 = yes)

As designed in [§12](#12-attachments). Separate approval for storage, scanner, schema needs (if any), and UI.

### Phase 3L — Staging validation and security audit

| Field | Content |
|---|---|
| Objective | Full functional run on staging with synthetic data (all browser specs, ar/en, 4 widths, axe); load baseline (booking concurrency under load, p95 latency); multi-tab session check (P3-SEC-25); external or independent penetration test (P3-D24); log review for PHI; incident-response tabletop exercise |
| Affected | Reports in `docs/security/` |
| Dependencies | 3B–3J (3K if enabled) |
| Database impact | None (staging data synthetic) |
| Security impact | Independent verification |
| Tests | As listed |
| Rollback | n/a |
| Acceptance | No open BLOCKER/HIGH (or each accepted in writing); report published |

### Phase 3M — Production readiness gate and go-live

| Field | Content |
|---|---|
| Objective | Provision production from the same IaC; apply migrations; bootstrap platform admin once; enable monitoring/alerts/on-call; go-live checklist; rollback plan rehearsed |
| Dependencies | 3L passed; legal review outcome acceptable (P3-D3) |
| Database impact | Production database created, migrations 001–004 applied by script |
| Security impact | Production posture as verified in staging |
| Tests | Production smoke (synthetic platform-owned test clinic, removed or clearly isolated afterwards), probe green, alert test |
| Rollback | Previous image; restore point taken before migration |
| Acceptance | See gate G-PROD below; owner sign-off |

---

## 18. Acceptance gates

| Gate | After | Must be true |
|---|---|---|
| G-3A | 3A | Decisions P3-D1, D2, D6, D7, D13 recorded; legal review started |
| G-SEC | 3B, 3C | GD-03/GD-15 closed or accepted in writing; startup guards prevent dev settings in Production; key rotation works; all suites green (unit, integration, frontend, browser), EF no pending changes, gitleaks clean |
| G-PLATFORM | 3D–3G | Email delivers in staging; transient DB failures never duplicate bookings (concurrency suites green); limiter consistent across instances (if enabled); logs JSON, PHI-free, alerts fire |
| G-STAGING | 3H–3J | Pipeline deploys staging from the same artifacts that would go to production; restore drill passed within proposed RTO; staging security checks (TLS, headers, DB private, client IP) pass |
| G-AUDIT | 3L | No open BLOCKER/HIGH (or written acceptance); pen-test findings triaged; load baseline recorded; incident runbook exercised |
| G-PROD | 3M | Legal/compliance review completed with no open blocking item; owner sign-off; on-call defined; backups verified in production; rollback plan rehearsed; `STATUS.md` updated. Only then may the product be described as "in production" — still **without** a compliance claim unless formally verified |

---

## 19. Phase 3A — decisions and security hardening (2026-10-08)

Phase 3A recorded the owner's decisions and implemented only the security hardening the owner approved. No database
change was made. §1–§18 above are the original plan; this section is authoritative where they differ.

### 19.1 What Phase 3A did and did not do

| Done | Not done (by instruction) |
|---|---|
| Server-side session validation for clinic **and** platform tokens (GD-03); reactivation no longer revives sessions (GD-15) | No hosting provider chosen, nothing deployed, no cloud configuration, no CI/CD deployment |
| Browser refresh-token hardening (P3-SEC-08) | No migration, table, column, index or seed change; the D7 SQL was not executed |
| Domain architecture requirement documented (P3-D6) | No distributed rate limiting (P3-D7); no retry policy code (P3-D8); no attachments/storage/scanner (P3-D9); no production email provider |
| MFA assessed → stopped; later **implemented** after the schema approval ([§19.5](#195-platform-admin-mfa-p3-d5--implemented-not-deployed)) | Phase 3B not started |

### 19.2 Decision record

The owner's Phase 3A brief numbered its decisions P3-D1…P3-D10, which **collides with this plan's own P3-D1…P3-D24 in
§16** (for example the plan's P3-D6 is "token revocation", the brief's P3-D6 is "domain architecture"). To avoid
ambiguity the decisions are recorded here as **3A-D1…3A-D10** (brief numbering) with the plan decision they answer.

| ID | Decision (owner, 2026-10-08) | Answers plan decision | Effect |
|---|---|---|---|
| 3A-D1 | Production hosting must support Saudi data-residency requirements. **No provider selected yet**; chosen after comparing suitable Saudi-hosted options | P3-D1 | Provider stays open. Blocks 3H/3I |
| 3A-D2 | Prioritise keeping application data, SQL Server, backups, logs, audit data, file/object storage and (where the provider permits) email-related data in Saudi Arabia. **No legal-compliance claim**; final residency requirements need review by a Saudi legal/compliance specialist | P3-D2 | Selection criterion for every provider (hosting, email, logging, backup) |
| 3A-D3 | GD-03: server-side session validation, reusing the existing session/refresh-token data and indexes, no new table or column | P3-D6 (Option D) | **Implemented** ([§19.3](#193-session-revocation-p3-d3-p3-d4-gd-03-gd-15)) |
| 3A-D4 | GD-15: suspended memberships/clinics must not become usable again through an old access token; reactivation must not revive sessions invalidated by suspension | P3-D6 | **Implemented** (same mechanism) |
| 3A-D5 | Platform administrators must use MFA before production; smallest secure implementation, no external identity provider; schema only with approval | P3-D7 | **Implemented** (two approved tables, migration 005; not deployed) ([§19.5](#195-platform-admin-mfa-p3-d5--implemented-not-deployed)) |
| 3A-D6 | Production frontend and API share a registrable domain: `app.<domain>` and `api.<domain>`. No real domains configured yet | P3-D13 (option A) | Requirement documented ([§19.6](#196-domain-architecture-requirement-3a-d6)) |
| 3A-D7 | No distributed rate limiting yet; keep the single-instance limiter. Production scaling addresses shared limiting later | P3-D9 | Unchanged. Production must run **one API instance**, or accept N× limits (P3-SEC-09), until this is revisited |
| 3A-D8 | SQL retry: connection-establishment retry allowed; safe read-only query retry allowed; **no** automatic retry around non-idempotent writes (booking, prescription, patient writes). Transient write failures fail safely with a retryable response. No idempotency table | (not in plan; refines P3-SEC-15) | Direction recorded, **not implemented**: the code has no `EnableRetryOnFailure` today, so it already complies; read-only/connection retry and the retryable (503) mapping are later work |
| 3A-D9 | Attachments stay disabled; no object storage, no malware scanning | P3-D15 | Unchanged (D3) |
| 3A-D10 | The product needs Saudi legal/compliance review before launch. No claim of PDPL, healthcare-regulatory or complete data-residency compliance | P3-D3 | External dependency; blocker for go-live |

Gate G-3A (§18) asks for P3-D1, D2, D6, D7, D13 to be recorded: D1, D2, D13 and D6 are recorded above; **D7 (MFA) is
recorded as "required; implemented after the schema approval, production prerequisites open"**.

### 19.3 Session revocation (3A-D3, 3A-D4; GD-03, GD-15)

**Session model, read from the source.** A *session* is a refresh-token family. `AuthService.IssueSessionAsync` creates
the family id (`FamilyId`) and puts it into the access token as `sid`; every refresh adds a new `RefreshTokens` row to
the same family and revokes the previous row in the same `SaveChanges` (so a rotating session always has exactly one
live row). Each row carries `UserId` and `TenantId` (null = platform plane). Every way a session ends already revokes the
family or the user's rows: logout, password change, password reset, invitation password, ending a session
(`DELETE /auth/sessions/{id}`), `switch-tenant` (old family), clinic suspension (`PlatformTenantService.SetStatusAsync`
revokes every row of the clinic), disabling a member (`StaffService`, rows of that user in that clinic) and refresh-token
reuse (family). What was missing was only the check on the access-token side.

**Rule.** On every authenticated request `TenantAccessMiddleware` calls
`TenantAccessResolver.CheckSessionAsync`, one SQL statement that returns whether

1. the user exists and `IsActive`, and
2. a `RefreshTokens` row exists with `FamilyId = sid`, `UserId = sub`, `TenantId = tid` (or null for platform tokens),
   `RevokedAtUtc IS NULL` and `ExpiresAtUtc > now` (index `IX_RefreshTokens_FamilyId`), and
3. (clinic tokens) the membership `mid` is still `Active` for the same user and clinic — the GD-02 check, now in the same
   statement instead of its own query.

A token without `sid`, with another user's `sid`, or with a `sid` from another clinic or the platform plane has no live
session. Signed tokens are still validated first (issuer, audience, lifetime, signature).

**Order of refusal** (so existing error codes are kept): membership → `401 NO_ACTIVE_MEMBERSHIP`; clinic status →
`403 TENANT_SUSPENDED`; session → `401 UNAUTHORIZED`; read-only → `403 SUBSCRIPTION_EXPIRED`. `/auth/*` skips the
membership and clinic-status rules except `GET /auth/me` (see behaviour changes). No new error code was needed for
sessions.

**Public endpoints.** On `[AllowAnonymous]` endpoints (login, refresh, public plan catalog, accept-invitation…) a
request whose session is dead is treated as **anonymous** (the identity is removed from the request) instead of being
rejected: a stale token left in the browser can never block signing in again, and a logged-out token can no longer
stand in as "signed in" when accepting an invitation for an existing account.

| Event | Before | After |
|---|---|---|
| Logout (cookie or body token) | Access token valid ≤ 15 min | `401 UNAUTHORIZED` on the next request, every endpoint |
| Password change | Old access token valid ≤ 15 min | Old token (including the caller's) and other devices → 401; the response carries a new session |
| Password reset / invitation password | ≤ 15 min | 401 at once |
| Ending another session | ≤ 15 min | That session's access token → 401 at once; others unaffected |
| Switching clinic | Old clinic token valid ≤ 15 min | Old token → 401; the new token works |
| Clinic suspension | Clinic requests 403 `TENANT_SUSPENDED` | Unchanged for the response code; sessions are now also dead |
| Clinic **reactivation** | Tokens issued < 15 min before suspension work again (GD-15) | They stay dead (families were revoked at suspension); a new sign-in is required |
| Disabled member | `NO_ACTIVE_MEMBERSHIP` on clinic endpoints; token still worked on other `/auth/*` | `NO_ACTIVE_MEMBERSHIP` on clinic endpoints; token now also dead on `/auth/*` (the seat's refresh tokens were revoked); the person signs in again to reach another clinic |
| Refresh rotation | n/a | Old and new access token of the same session both work; replaying a rotated refresh token kills the family **and** its access tokens |
| Platform tokens | Never re-checked | Checked like clinic tokens: logout / ended session → 401 at once |
| Other clinic | Tenant filter, composite FKs | Unchanged; additionally a session of clinic A can't be presented for clinic B |

**Cost.** Measured with an EF command interceptor (throw-away probe, not committed): a clinic request issues 2 queries
before the handler — session+membership merged, then tenant access — exactly as before (membership, tenant access); a
platform request went from 0 to 1; `/auth/*` (other than `/me`) from 0 to 1; `/auth/me` stays at 2 (the tenant lookup is
now done by the middleware and cached for the controller).

**Behaviour changes, deliberately accepted** (each is covered by a changed or new test):

1. Tokens of a session that was ended are refused everywhere, including `/auth/*` (a disabled member can no longer call
   `/auth/tenants` or `/auth/switch-tenant` with the old token; the UI already sent that person back to sign-in because
   refresh failed).
2. `GET /auth/me` for a **suspended** clinic answers `403 TENANT_SUSPENDED` (was `200` with `access = "Suspended"`).
   Suspension now ends the sessions, so no live session remains to answer; `403 TENANT_SUSPENDED` keeps the UI's one
   "clinic suspended" screen. API reference updated.
3. A revoked token on a public endpoint is anonymous (e.g. the public plan catalog answers `200` where a suspended
   clinic's token used to get `403`).
4. Switching clinic (or changing the password) on one tab now moves/ends the same cookie-backed session for other open
   tabs immediately: they refresh into the new session instead of continuing on their old token for up to 15 minutes.

**Known limits.** (a) The check is per request against the database; a deliberate in-process cache (the "few seconds"
option in §3.2) was *not* added. (b) `TenantAccessMiddleware` runs before the rate limiter (as it did for GD-02), so a
holder of a revoked-but-signed token (≤ 15 min old) costs one indexed query per request outside the limiter; the
signature check still stops forged tokens before any query. Worth revisiting with shared rate limiting.

### 19.4 XSS / refresh-token exposure (P3-SEC-08)

**Finding.** Any script on the web origin could call `POST /auth/refresh` (cookie sent automatically) with the header
`X-Client-Type: mobile` — which CORS allowed — and receive the refresh token in the response body, bypassing the
httpOnly cookie. CSP was the only barrier.

**Usage check before changing anything.** The web app never sends `X-Client-Type` (searched `frontend/src` and
`frontend/e2e`). There is **no native client in the repository**; the mode exists in `AuthController`, is described in
`docs/04-security-model.md` as the design for future mobile apps, and is used only by integration tests. It was therefore
kept (not deleted) and separated from the browser.

**Fix** (`NativeClient.cs`, `AuthController.cs`, `Program.cs`):

1. Native mode is refused for anything that looks like a browser: a request with `X-Client-Type: mobile` **and** an
   `Origin` header or any `Sec-Fetch-*` header gets `403 CLIENT_TYPE_NOT_ALLOWED`. Browsers add those headers to every
   cross-origin and every POST request and page script can neither set nor remove them (forbidden header names); native HTTP
   stacks send neither. The check is an authorization filter on the whole `AuthController`, so it runs before model binding and before
   any rotation, login or registration — a refused call has no side effect (verified: the token is still valid afterwards).
2. CORS no longer allows the `X-Client-Type` header, so a cross-origin browser call fails at the preflight before it is sent.
3. Each client type has one transport: web presents the refresh token **only** through the cookie (a body token is
   ignored for refresh and logout); native presents it **only** in the body.
4. Unchanged: cookie `rt` is `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`, rotation, reuse detection and family revocation.

**Residual risk (stated honestly).** Script running in the page can still call the API *as the user* while the page is
open (it can refresh through the cookie and use the access token) — inherent to any cookie-based session. What it can no
longer do is take a long-lived credential away: it cannot read the refresh token, so the session ends when the tab's
access is gone or the session is revoked (and revocation is now immediate, §19.3). CSP stays as defence in depth. A
non-browser attacker can send any headers, but needs the refresh token first.

**Mobile contract (for a future native app).** Send `X-Client-Type: mobile`, no `Origin`/`Sec-Fetch-*`; refresh token in
the response body, presented in the request body. A hybrid/WebView app that sends `Origin` is a browser for this purpose and
must use the cookie flow (document this when the mobile app is designed).

### 19.5 Platform-admin MFA (3A-D5) — implemented, not deployed

**Status (2026-10-08).** Earlier in 3A this was *stopped*: a real second factor needs new persisted data, so no code was
written. The owner then reviewed a design (APPROVE WITH CHANGES) and approved exactly **two new tables**, which are now
implemented as migration 005 `PlatformAdminMfa` (no other schema change; nothing deployed, nothing applied to Development or
Production). Operations: [`docs/operations/platform-admin-mfa.md`](operations/platform-admin-mfa.md).

**Scope.** Every account with `PlatformRole != None`. Clinic users are unchanged. TOTP (RFC 6238: HMAC-SHA1, 6 digits, 30 s,
previous/current/next step) plus 10 one-time recovery codes. Email is never a factor (it is the password-reset channel).

**Schema (exact).**

```text
UserTotpFactors    Id (PK), UserId (FK Users, CASCADE), SecretProtected varbinary(256) NOT NULL,
                   LastUsedTimeStep bigint NULL, CreatedAtUtc datetime2(3) NOT NULL        unique index (UserId)
UserRecoveryCodes  Id (PK), UserId (FK Users, CASCADE), CodeHash char(64) NOT NULL,
                   CreatedAtUtc datetime2(3) NOT NULL, ConsumedAtUtc datetime2(3) NULL     unique index (UserId, CodeHash)
```

Platform-owned (no `TenantId`). A factor row exists only after the user proved possession of the authenticator, so there is
no "pending" or "disabled" row; removing MFA deletes the rows and the audit log keeps the history. (`datetime2(3)` is the
project's convention for instants.)

**As built, and where it differs from the design.**

| Topic | Built |
|---|---|
| Pre-MFA state | **No session at all**: password-only login of a platform account answers `409 MFA_REQUIRED` (authenticator exists) or `409 MFA_ENROLLMENT_REQUIRED` with a short-lived (5 / 10 min) Data-Protection token. No access token, refresh token, cookie or session family is created. This is why no `RefreshTokens` column was needed (the earlier claim here that an `amr` claim needs no schema was wrong for the existing code, which rebuilds claims on every refresh). |
| Enrolment | Stateless: the pending secret travels inside an encrypted, user-bound, purpose-bound token until the user enters a valid code. Then, in one transaction: factor + 10 recovery codes inserted, **all earlier sessions revoked**, and the first session issued. A unique index decides concurrent enrolments. |
| Secret at rest | ASP.NET Core Data Protection, purpose `ClinicSaaS.Mfa.TotpSecret.v1` + the user id. No custom crypto. Production needs a persisted key ring **outside the database**, protected by a certificate (see §20 prerequisites); otherwise the API does not start. |
| Replay | `UPDATE … WHERE LastUsedTimeStep IS NULL OR < step`; one affected row = success. |
| Recovery codes | 80 bits (16 base32 chars), SHA-256 only, `UPDATE … WHERE ConsumedAtUtc IS NULL`; regenerate needs a current **TOTP** (a recovery code cannot) and replaces all ten. |
| Brute force | Existing `AccessFailedCount`/`LockoutEndUtc`, no new counter. An attempt is **claimed first** in one atomic conditional UPDATE (so N parallel guesses are granted at most the threshold) and success resets the counter; the password step does **not**. MFA lockout 60 min (`Auth:MfaLockoutMinutes`). The `Auth` rate-limit policy covers `/auth/mfa/*`. One uniform `INVALID_MFA_CODE` for wrong, replayed and reused codes. |
| Sessions | `CheckSessionAsync` also requires a factor for `PlatformRole != None` (legacy sessions, deleted rows), and a refresh token of such an account without a factor is refused and its family revoked. Logout, password change/reset, suspension and revocation are unchanged. |
| Removal | Session + current password + TOTP or recovery code: factor and codes deleted, **every** session revoked, no new session; the next sign-in enrols again. |
| Operator reset | **No API** (no support/other-admin reset). A documented break-glass SQL runbook with out-of-band verification and two approvers writes the `auth.mfa.reset` audit event. |
| Audit | `auth.mfa.{challenge_issued, enrollment_started, enrollment_failed, enabled, challenge_failed, challenge_succeeded, recovery_code_used, recovery_codes_regenerated, disabled, reset}` plus the existing `auth.login.succeeded` / `auth.account.locked`. Never a secret, code, URI, token or password. |
| API | `POST /auth/mfa/{enroll/start, enroll/complete, verify, recovery-codes/regenerate, remove}` and the two 409 login answers ([docs/05](05-api-reference.md)). |
| UI | Sign-in challenge, enrolment (setup key + `otpauth://` link; **no QR image**, no QR library was added), recovery codes shown once, and a security page for replacing the codes / removing MFA. |

**Still open (not code).** Production key-ring storage and certificate custody (depends on the hosting decision), the break-glass
procedure being agreed and practised, monitoring/alerting on MFA events (Phase 3C), a second platform admin (no creation path
exists), and the residual risks below.

**Residual risks.** A real-time phishing relay of a code is not stopped by TOTP (WebAuthn would); a stolen live session or
refresh token is not stopped by MFA (reuse detection and revocation are); an attacker who already holds the password can still
guess 6-digit codes at 5 per hour (about 1% a month sustained, no alerting yet); database *write* access or app-server
compromise defeats MFA; the Data Protection key ring is the trust root for the secrets and the enrolment/challenge tokens.

**Verified locally (2026-10-08, Windows, LocalDB 17, Chromium 1.63; nothing applied to Development or Production).** Backend
Release build 0 warnings · unit **106/106** (26 new: RFC 6238 vectors, steps ±1/±2, replay, recovery-code format and entropy) ·
integration **515/515** (464 existing, all still green, plus 51 new: 40 behaviour tests in `PlatformMfaTests`, 5 schema tests in
`PlatformMfaSchemaTests` incl. an Up→Down→Up round-trip that fingerprints every other table/column/index/FK/check, 6 production
guard cases for the key ring) · EF no pending model changes · no vulnerable NuGet packages · frontend lint/typecheck clean ·
vitest **203/203** (17 new) · production build OK · `npm audit --omit=dev` 0 · browser suite **60/60** (54 existing + 6 new
`mfa.spec.ts`, Arabic RTL and English LTR, axe WCAG 2.2 AA on the enrolment, recovery-code, challenge and security screens).
Tests were given teeth by breaking each protection in turn (replay check, recovery-code single use, the "not locked" attempt claim,
the password step resetting the counter, the session check, the refresh check): each mutation failed the matching tests and was
restored. Existing platform-admin tests were not weakened: they now sign in through the real MFA endpoints (enrolling on first use);
only the fixture may clear `LastUsedTimeStep` so one authenticator can sign in repeatedly inside one 30-second step.

### 19.6 Domain architecture requirement (3A-D6)

Production must serve the web app and the API from **sibling subdomains of one registrable domain**: `app.<domain>` and
`api.<domain>` (plan §2.3, option A). Reasons that are properties of the code, not preferences: the refresh cookie is
`SameSite=Strict` and host-only on the API host — requests from `app.<domain>` to `api.<domain>` are *same-site*, so the cookie
is sent; with unrelated domains it would be cross-site and refresh would break (and weakening to `SameSite=None` is
not acceptable). CORS must list exactly `https://app.<domain>` (`Cors:AllowedOrigins`); the web build's `NEXT_PUBLIC_API_URL` must be
`https://api.<domain>` (and the CSP `connect-src` follows it). `<domain>` must be a registrable domain, not a public suffix
(for example `example.com.sa`, not `com.sa`). No other subdomain of it may host untrusted or user-generated content
(same-site subdomains weaken `SameSite`). **No real domain, DNS or certificate has been configured.**

### 19.7 Remaining production blockers (real ones)

| # | Blocker | Status after 3A |
|---|---|---|
| 1 | Hosting provider and region (Saudi data residency) not chosen; legal/compliance review not started or done (P3-SEC-02, 3A-D1/D2/D10) | Open — external |
| 2 | Platform-admin MFA (P3-SEC-04) | Code done ([§19.5](#195-platform-admin-mfa-p3-d5--implemented-not-deployed)); open: key-ring custody, break-glass procedure, alerting |
| 3 | Production email provider (P3-SEC-01); the API intentionally refuses to start without one | Open |
| 4 | Backups, tested restore, incident-response/breach runbook (P3-SEC-03) | Open |
| 5 | Client IP behind the load balancer: `KnownNetworks` support (P3-SEC-05) | Open (3C) |
| 6 | One-time tokens travel in URL query strings and will appear in proxy/LB access logs (P3-SEC-06) | Open (3C/3F: log redaction or a POST-based hand-off) |
| 7 | Production configuration guards and signing-key rotation (P3-SEC-12, G-SEC) | Open (3C) |

Closed in 3A: P3-SEC-07 (GD-03), P3-SEC-08 (XSS/refresh), P3-SEC-14 (GD-15). Still open but not blockers: P3-SEC-09
(single instance until shared limiting), P3-SEC-10, P3-SEC-11 (pending P3-D17), P3-SEC-13, P3-SEC-15 (retryable 503), the
rest of the table in §3.1.

### 19.8 Verification (Windows, LocalDB 17, Chromium 1.63; executed 2026-10-08)

| Check | Result |
|---|---|
| Backend Release build | 0 warnings, 0 errors |
| Unit tests | 80/80 |
| Integration tests | 359/359 (331 before + 28 new: `SessionRevocationTests` 14, `RefreshTokenExposureTests` 14; 6 existing tests changed deliberately, see §19.3) |
| Mutation checks | Disabling the session check fails 12 of the 14 new session tests (the other 2 don't depend on it); removing the browser guard fails all 8 guard cases |
| Frontend lint / typecheck / vitest / production build | clean / clean / 178/178 / OK |
| Browser suite (`npm run test:e2e`) | 52/52 (47 before + 5 new: `e2e/sessions.spec.ts` — logout, password change, reactivated clinic; `e2e/refresh-token.spec.ts` — normal cookie refresh, in-page refresh-token attempt; plus the account spec now asserts the ended session's token is dead) |
| `npm audit --omit=dev`; NuGet `--vulnerable --include-transitive` | 0 vulnerabilities; none |
| gitleaks (full history, 3A commits included) | no leaks. The working-tree scan flags only git-ignored build output (`bin/`, `obj/`, `.next/`, dev mailbox), 0 in tracked files. Docker is not installed locally; CI remains the second line |
| EF `has-pending-model-changes` | none |
| Database gate | migrations added 0, changed 0 (001–004); schema changes 0; seed changes 0; D7 SQL not executed |

Test-environment note: the browser suite ran against a **new throw-away local database** (`ClinicSaaS_E2E_3a`, created by the
existing migrations at API start, as `e2e/README.md` recommends). `ClinicSaaS_Dev` and `ClinicSaaS_Dev_gap` were not touched.

---

## 20. Phase 3B — production readiness without deployment (2026-10-08)

Phase 3B made the application *ready to be deployed safely* and wrote down what a deployment must provide. Nothing was
deployed, no provider, region, domain or secret was chosen, no infrastructure was provisioned, and the database schema,
migrations and seed data were not touched. It covers parts of the roadmap items 3C (configuration), 3D (email boundary),
3E (SQL resilience) and 3G (log safety), plus the operations documents of 3J. Operations documents live in
[`operations/`](operations/README.md).

### 20.1 What was done, by step

| Step | Done | Where |
|---|---|---|
| 2 Production configuration | Start-up guard for production-like environments: lists every unsafe key (never a value) — JWT key, token lifetime, SQL connection string, CORS, `AppBaseUrl`, `AllowedHosts`, email provider, auto-migrate, test-grade rate limits and lockout values, multi-instance limiter, proxy trust, log levels. `Testing` is honoured only inside a test host, so a deployment cannot disable the guards by naming its environment "Testing". `appsettings.Production/Staging.json` list what must be supplied | `ProductionConfigurationGuard`, `RuntimeProfile`, `Program.cs` |
| 3 JWT keys | Placeholder / short / low-variety / repository-test keys refused in production-like environments; `Jwt:PreviousSigningKeys` (validation only) enables an overlap rotation; HS256 only (tested). Rotation needs no schema because sessions do not depend on the key. Procedure documented. Automated rotation is a deployment task | `JwtOptions`, `operations/deployment-prerequisites.md` §4 |
| 4 Cookies / headers | Audit: refresh cookie already `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`; CSP, nosniff, frame, referrer, permissions headers already present (web and API). Added: API HSTS 2 years + subdomains (matches the web app); `no-referrer` + `no-store` on the three one-time-link pages; CSRF defence in depth — `/auth/refresh` and `/auth/logout` refuse a browser `Origin` outside `Cors:AllowedOrigins`. CSP unchanged (no weakening); full browser suite in Arabic and English passes | `NativeClient.cs`, `next.config.ts` |
| 5 Client IP / proxy | Generic trust model: `KnownProxies`, `KnownNetworks` (CIDR; catch-alls rejected), `ForwardLimit`, or `NoProxy`; forwarded headers honoured only from configured peers, only the closest trusted proxy's entry used, framework loopback trust removed in production-like environments. Spoofing tested (untrusted peer, prepended entries, subnet, direct, rate limiting by resolved address). **What must be supplied later:** the real proxy/LB addresses or subnet, the number of hops, confirmation that it overwrites client `X-Forwarded-*`, and verification on the real network | `ForwardedHeadersSettings` |
| 6 One-time tokens | Audited ([`operations/sensitive-data-in-logs.md`](operations/sensitive-data-in-logs.md)). Emailed links now carry the token in the URL **fragment** — never sent to any server, so it cannot reach proxy/LB/web-server access logs or `Referer`; the web app reads it once, strips it from the address bar, and still accepts the old `?token=` form. The API only ever read tokens from POST bodies (tested). No application log contains a password, token, key, national ID or mobile number (tested with logging at Verbose) | `OneTimeLinks`, `one-time-token.ts` |
| 7 SQL resilience | Connection opens retried (default 2 attempts, original `SqlException` preserved); plain `SELECT`s outside a transaction retried (default 3); **writes, locks and transactions never**. Transient failures → `503 TEMPORARILY_UNAVAILABLE` + `Retry-After: 5`, no internals. Fault-injection tests against SQL Server; the whole concurrency/mutation suite is green | `SqlResilience`, `TransientFailures` |
| 8 Errors / logs | Audit: no stack traces, SQL text, paths, connection strings or JWT detail reach callers (tested with an exception that contains all of them); logs carry correlation, user and tenant ids and no bodies or secrets; tenant-isolation violations stay `Critical`. Email failures are logged as purpose + recipient domain + exception types only | `GlobalExceptionHandler`, `EmailDelivery` |
| 9 Rate limiting | Unchanged and documented honestly: per process. The guard refuses more than one API instance unless `RateLimiting:AcceptPerInstanceLimits=true` is set as a recorded decision. No shared store was built (3A-D7) | `ProductionConfigurationGuard`, `operations/deployment-prerequisites.md` §8 |
| 10 Email | Production-like start refuses a missing provider or `DevMailbox` (unchanged, now reported by the guard too); `Testing`/`Staging` cannot use the dev mailbox. Delivery after commit is best-effort: a provider failure no longer yields a 500 after the data was written. No provider chosen; requirements listed in the deployment prerequisites | `EmailDelivery` |
| 11 Backups | Requirements written: **NOT IMPLEMENTED — OPERATIONAL REQUIREMENT** | [`operations/backup-restore.md`](operations/backup-restore.md) |
| 12 Incidents | Runbook with the ten scenarios; break-glass SQL (data change, not schema) documented but never run; legal notification explicitly left to Saudi legal/compliance review | [`operations/incident-response.md`](operations/incident-response.md) |
| 13 Deployment docs | Domain structure (`app.<domain>` / `api.<domain>`, same registrable domain, why), TLS, configuration reference, secrets, SQL, proxy, migrations/rollback, health, logging requirements | [`operations/deployment-prerequisites.md`](operations/deployment-prerequisites.md) |
| 14 Health | Liveness never touches the database; readiness reports it; both answer a bare word. Contract unchanged; now covered by tests | `Program.cs` |

### 20.2 New findings from the audit

| Finding | Severity | Status |
|---|---|---|
| Environment name `Testing` switched off every production guard (email, storage, migration, and any new one) in a deployed API | HIGH | **Fixed** — honoured only inside a test host |
| Patient searches put a mobile number or national ID in URL query strings (`/patients?search=`, `/patients/duplicates?…`); reverse-proxy / LB / WAF / APM logs record URLs by default | MEDIUM | Application logs are clean (tested). Needs infrastructure configuration; changing the API is a breaking contract change, deferred |
| SqlClient's retry provider wraps exhausted retries in an `AggregateException`, which broke EF's "does the database exist?" probe (a fresh database could not be created) | — | Found by the tests before commit; connection retry rewritten to keep the original exception |
| The framework's request logging at Information prints full URLs including those query strings | MEDIUM | Production-like start refuses it (guard); a test shows what would leak |
| `LoadActiveMembershipsAsync` (three `Include`s) makes EF log a `MultipleCollectionIncludeWarning` at every login | INFO | Not changed (query behaviour); noise in production logs — consider `AsSplitQuery` later |

### 20.3 Behaviour changes to know about

1. A production-like API **will not start** until the configuration in the deployment prerequisites is supplied — and
   cannot start at all until a real email provider adapter exists (by design since Gate C).
2. Emailed one-time links use `#token=`; links already sent with `?token=` keep working until they expire.
3. `/auth/refresh` and `/auth/logout` answer 403 `FORBIDDEN` to a browser request whose `Origin` is not in
   `Cors:AllowedOrigins`. Requests without an `Origin` are unaffected.
4. New `503 TEMPORARILY_UNAVAILABLE` + `Retry-After` for transient database/lock failures (additive contract).
5. Registration, invitations, verification resend and password reset no longer fail with 500 when the email provider
   fails; the email is not queued (no outbox without a table) and the failure is logged.
6. API HSTS is two years with subdomains; production-like environments need `AllowedHosts` and trusted-proxy configuration.
7. `AuthService` and `StaffService` take an `ILogger` (DI-resolved).

### 20.4 Not implemented (by instruction or because it needs approval)

MFA key-ring custody and the break-glass procedure (§19.5) · hosting provider and region · production email provider adapter · backups and
restore (requirements only) · infrastructure, networking, TLS termination, DNS · monitoring and alerting infrastructure,
central log store, telemetry · distributed rate limiting · CI/CD deployment · attachments · idempotency keys / outbox /
token cleanup job (each needs a table or data deletion) · key-rotation automation · account-enumeration decisions (P3-D17).

### 20.5 Verification (Windows, LocalDB 17, Chromium 1.63; executed 2026-10-08)

| Check | Result |
|---|---|
| Backend Release build | 0 warnings, 0 errors |
| Unit tests | 80/80 |
| Integration tests | 464/464 (359 before + 105 new: configuration guard, environment rule and real-host start-up 59, proxy/origin/key rotation/algorithm 12, log capture 2, one-time links 2, SQL resilience 22, 503/health/email failure 8; plus 6 existing tests that start a production-like host now begin from valid production settings) |
| Frontend lint / typecheck / vitest / production build | clean / clean / 186/186 (8 new) / OK |
| Browser suite (`npm run test:e2e`) | 54/54, run twice (52 before + 2 in `one-time-links.spec.ts`; Arabic and English, 4 widths, axe, sessions, suspended tenant, disabled member, password change, logout, refresh, in-page token attempt) |
| `npm audit --omit=dev` / NuGet `--vulnerable --include-transitive` | 0 / none |
| gitleaks (full history, Phase 3B commits included) | no leaks (Docker not installed locally; CI is the second line) |
| EF `has-pending-model-changes` | none |
| Database gate | migrations added 0, changed 0; schema changes 0; seed changes 0; D7 SQL not executed. The only file added under `Persistence/` is `SqlResilience.cs` (interceptors), not a migration, snapshot, configuration or seed |

Security checks covered by named tests: cross-tenant and cross-branch isolation (existing isolation suites, green);
revoked session, suspended tenant, disabled member (`SessionRevocationTests`, Gate D suites); forged JWT, `alg=none`,
other algorithm (`Forged_or_tampered_tokens…`, `Only_HS256…`); refresh-token abuse and browser/native separation
(`RefreshTokenExposureTests`, `SessionRevocationTests`); forwarded-IP spoofing (`ProxyOriginAndKeyTests`); sensitive-token
logging (`SensitiveLoggingTests`, `OneTimeLinkTests`); production configuration guards (`ProductionConfigurationGuardTests`).

### 20.6 Remaining production blockers

| Severity | Item |
|---|---|
| **BLOCKER** | Hosting provider/region undecided, data-residency requirements unreviewed, **Saudi legal/compliance review** not done (P3-SEC-02, 3A-D10) |
| **BLOCKER** | No production email provider adapter (P3-SEC-01); the API correctly refuses to start without one |
| **BLOCKER** | No backups and no tested restore (P3-SEC-03); requirements are written only |
| **BLOCKER** | No deployment infrastructure or pipeline (nothing to deploy to): network, TLS, compute, SQL Server, secret store, CI/CD |
| **HIGH** | Platform-admin MFA implemented but not deployable until the key ring is provisioned and the break-glass procedure agreed (P3-SEC-04) |
| **HIGH** | No monitoring, alerting, central logging or on-call; incident runbook written but not exercised |
| **HIGH** | No independent penetration test / staging validation (plan gate G-AUDIT) |
| MEDIUM | Trusted-proxy configuration is generic and tested but unverified on the real network (P3-SEC-05) |
| MEDIUM | Personal data (mobile, national ID) in search query strings vs proxy/WAF/APM logs — infrastructure configuration required |
| MEDIUM | Rate limiting is per process → exactly one API instance (P3-SEC-09) |
| MEDIUM | Failed emails are not queued (no outbox); expired tokens/codes never cleaned up (P3-SEC-13); account enumeration/lockout policy undecided (P3-SEC-11) |
| LOW | GD-04 (invitation acceptable while suspended); least-privilege script not valid on every managed SQL offering and `db_datawriter` allows hard deletes (P3-SEC-18); no platform-admin UI (P3-SEC-20) |
| INFO | EF multiple-include warning on login; GD-14 native dialog focus; dev-only npm advisory; CSP `style-src 'unsafe-inline'` (documented) |

---

## 21. Phase 3C — security operations and readiness (2026-10-08)

Phase 3C closed what can be closed inside the application without a schema change, a product decision or infrastructure,
and wrote down the rest. The original 3C configuration items (§17: production settings, guards, `KnownNetworks`,
`AllowedHosts`, key rotation, SQL `Encrypt=True`) were already delivered in 3B (§20.1). Nothing was deployed; no provider,
region, domain, credential, price or secret was chosen; the schema, migrations, seed data and stored grants were not touched
(the MFA tables of §19.5 remain the only schema change of Phase 3); D7 SQL was not executed.

### 21.1 What was done

| Item | Done | Where |
|---|---|---|
| Monitoring hooks / MFA alerting / security alerts | Security-relevant audit rows are mirrored to the log **after they are saved** (event 4100 `SecurityAudit`, Warning for alert-worthy actions such as lockout, refresh-token reuse, MFA failures, recovery-code use, MFA removal; Information for the rest of the security trail). Identifiers only — no audit details, IP, user agent, email or mobile; clinical activity is never mirrored. Stable event ids for rate-limit refusals (4101), email failures (4102), tenant-isolation blocks (4103), bootstrap secrets left behind (4104), an unreadable MFA key ring (4105), database 503s (4106) and unhandled 500s (4107) | `SecurityLogEvents`, `AuditLogger`, `RateLimiting`, `GlobalExceptionHandler`, `MfaService`, `EmailDelivery` |
| Production logging | `Observability:ConsoleLogFormat=json` (set in the repo's Staging/Production settings): one JSON object per line with all properties; every line tagged `Application`, `Environment`, `Version` (build commit). No new package (Serilog's own formatter) | `Program.cs`, `appsettings.{Staging,Production}.json` |
| Alert rules | Proposed rules A1–A17 with priorities, first actions and staging triggers | [`operations/monitoring-and-alerting.md`](operations/monitoring-and-alerting.md) |
| P3-SEC-17 bootstrap secrets | Each start with `Bootstrap:*` still set after the account exists logs warning 4104 (never the value) | `PlatformBootstrap` |
| GD-04 (P3-SEC-19) | Accepting an invitation for a suspended/closed clinic answers 403 `TENANT_SUSPENDED` **before anything changes** (no password set, link not consumed, seat still pending); the same link works after reactivation. Read-only (ended period) clinics unchanged | `AuthService.AcceptInvitationAsync` |
| P3-SEC-25 multi-tab | **Reproduced** in Chromium: four tabs loading together got 401 from reuse detection and the whole session was revoked (2 of 3 runs). Fixed in the web client: refreshes take turns across tabs through the Web Locks API (each tab then presents the cookie its predecessor received). The server's rotation and reuse detection are unchanged | `frontend/src/lib/api/client.ts` |
| GD-14 (P3-SEC-22) | Closed as accepted platform behaviour: the native modal dialog keeps page content inert and lets Tab reach the browser's own toolbar (no keyboard trap; WCAG 2.1.2/2.4.3 satisfied); trapping focus in script would remove access to the browser UI | — (no code change) |
| Least privilege | Reviewed `least-privilege.sql`: the MFA tables are covered by the existing role memberships (the app needs `DELETE` on them for MFA removal); `AuditLogs` stays append-only. Adapting the script to a managed offering stays **[DB-APPROVAL]** (P3-SEC-18) | — |
| UI review | Screens reviewed in Arabic and English at 375/1440 px (today, appointments, patients, visit, prescription print, staff, subscription, login): restrained, consistent, no decorative noise; plan prices are not invented. One defect fixed: the English patient-search placeholder was clipped | `messages/en.json` |
| Documentation | Go-live checklist (what/why/where/who/verify/pre-go-live test for every infrastructure item, plus the owner's sequence); the damaged §11 of the deployment prerequisites repaired; README configuration table now lists the MFA key-ring keys; API reference, sensitive-data audit, Appendix A1/A2/A4 | [`operations/go-live-checklist.md`](operations/go-live-checklist.md) and the files named |
| UAT | Technical UAT of every area in the brief, mapped to executed tests | [`security/PHASE3-UAT.md`](security/PHASE3-UAT.md) |

### 21.2 Behaviour changes

1. `POST /auth/accept-invitation` for a suspended or closed clinic: 403 `TENANT_SUSPENDED` (was 204). The existing test that
   pinned the old behaviour (`QaGateDTests…GD-04`) was replaced deliberately by one asserting the new behaviour and the
   reactivation path.
2. Tabs of one browser no longer refresh the session at the same moment (no visible change except that the session survives).
3. New log lines (security trail, rate-limit refusals) and, in Staging/Production, JSON console output. Text output in
   Development is unchanged.

### 21.3 Findings register after Phase 3C

**CLOSED** — fixed in code and tested, or closed by a recorded decision.

| ID | Finding | How |
|---|---|---|
| P3-SEC-04 (code) | Platform-admin MFA | §19.5; infrastructure prerequisites listed under BLOCKED |
| P3-SEC-05 (code) | Client IP behind proxies | §20 (real-network verification under BLOCKED) |
| P3-SEC-06 | One-time tokens in URLs | §20 (fragment links) |
| P3-SEC-07 / -14 | GD-03 / GD-15 | §19.3 |
| P3-SEC-08 | Refresh token reachable from page script | §19.4 |
| P3-SEC-09 | Per-instance rate limits | Decision 3A-D7 (one instance); the guard enforces it |
| P3-SEC-10 | Email failure → 500 after commit | §20 (best-effort send; queueing under BLOCKED) |
| P3-SEC-12 | Signing-key rotation | §20 |
| P3-SEC-15 | Lock timeout / transient SQL → 500 | §20 (503 + Retry-After) |
| P3-SEC-16 | `AllowedHosts: *` | §20 (guard) |
| P3-SEC-17 | Bootstrap secret left configured | 3C warning 4104 |
| P3-SEC-19 | GD-04 | 3C fix |
| P3-SEC-22 | GD-14 | 3C, accepted platform behaviour |
| P3-SEC-25 | Two tabs refreshing end the session | 3C fix + browser test |
| 3C-01 | No monitoring hooks for MFA/security alerting | 3C event ids + alert rules |
| 3C-02 | `deployment-prerequisites.md` §11 duplicated/truncated; README lacked the MFA key-ring keys | 3C documentation |
| 3C-03 | English patient-search placeholder clipped | 3C copy fix |

**BLOCKED** — needs a decision, approval or infrastructure that is not the application's to provide.

| ID | Finding | Blocked by |
|---|---|---|
| P3-SEC-01 | No production email provider adapter | Provider choice (P3-D4), then plan task 3D |
| P3-SEC-02 | Hosting, region, data residency, legal review | Owner + Saudi legal review (3A-D1/D2/D10) |
| P3-SEC-03 | Backups, tested restore | Infrastructure (backup-restore.md) |
| P3-SEC-04 (infra) | MFA key ring storage + protection certificate custody; break-glass practised; second platform admin (no creation path) | Infrastructure; owner decision on a platform-admin creation path |
| P3-SEC-05 (infra) | Trusted-proxy values verified on the real network | Infrastructure |
| P3-SEC-10 (queue) | Failed emails are not queued | Outbox needs a table **[DB-APPROVAL]** (P3-D5) |
| P3-SEC-11 | Account enumeration (QA-06, QA-13) and targeted lockout | Owner decision P3-D17 (standing deferral) |
| P3-SEC-13 | Expired token/code cleanup, audit retention | Deletes data — explicit approval (P3-D18) |
| P3-SEC-18 | Least-privilege script on managed SQL; `db_datawriter` allows hard deletes | **[DB-APPROVAL]** + SQL offering |
| P3-SEC-24 | `/health/ready` reachable only from LB/monitoring | Infrastructure |
| 3B-PII | Mobile/national ID in patient-search query strings vs edge logs | Edge log configuration, or an approved breaking API change |
| G-AUDIT | Independent penetration test, staging validation | Infrastructure + tester (P3-D24) |
| Monitoring | Log store, alert delivery, on-call | Provider (P3-D8) + owner |

**OPEN** — accepted for now, not blocking, no action without a decision.

| ID | Finding | Note |
|---|---|---|
| P3-SEC-20 | No platform-admin UI | Product scope; operators use the platform API |
| P3-SEC-21 | CSP `style-src 'unsafe-inline'` | INFO, documented |
| P3-SEC-23 | Dev-only `braces` advisory via `eslint-config-next` | INFO; lint tooling only, never shipped; production dependencies 0. The only fix is a forced breaking change |
| GD-07 | Legacy prescriptions without a visit are clinic-wide | INFO; development/test data only |
| GD-11 | Dormant stored permission grants | Owner deferral (with D7); D7 SQL not executed |
| P3-D19 | `RequireConfirmedEmail` in production | Owner decision (recommended `true` for staff) |
| 3B-EF | EF `MultipleCollectionIncludeWarning` at login | INFO; log noise, not changed (query behaviour) |

### 21.4 Verification (Windows, LocalDB 17, Chromium; executed 2026-10-08)

| Check | Result |
|---|---|
| Backend Release build | 0 warnings, 0 errors |
| Unit tests | 106/106 |
| Integration tests | 518/518 (515 + 3 new `MonitoringEventsTests`; the GD-04 test replaced; the 503 test now also asserts event 4106) |
| Mutation checks | Removing the GD-04 check fails the GD-04 test; logging before the save (instead of after) fails the security-event test |
| EF `has-pending-model-changes` | none |
| Frontend typecheck / lint / vitest | clean / clean / 204/204 (1 new: refresh runs inside the cross-tab lock) |
| Browser suite (`npm run test:e2e`, fresh throw-away database) | **61/61, run twice** (60 + the multi-tab test). Before the fix the multi-tab test failed 2 of 3 runs; after it, 5 of 5 |
| `npm audit --omit=dev` / NuGet vulnerable (incl. transitive) | 0 / none |
| gitleaks | no leaks — on the staged files of every 3C commit before it was made, and over the full history (MFA and 3C commits included) |
| Database gate | migrations added 0, changed 0; schema/seed/grant changes 0; D7 SQL not executed; migration 005 applied to throw-away databases only |

### 21.5 What remains for go-live

Everything left is external: the decisions and infrastructure in [`operations/go-live-checklist.md`](operations/go-live-checklist.md).
Ranked:

| Priority | Item |
|---|---|
| **Blocker** | Hosting provider/region + Saudi legal/compliance review (3A-D1/D2/D10) |
| **Blocker** | Production email provider adapter (needs the provider choice) |
| **Blocker** | Backups with a tested restore (within the proposed RTO) |
| **Blocker** | Deployment infrastructure and pipeline (staging first): network, TLS, compute, SQL Server, secret store, CI/CD |
| **High** | MFA key ring + protection certificate provisioned, custody named; break-glass rehearsed; second platform admin decided |
| **High** | Log store, alert rules A1–A17 live, on-call named |
| **High** | Independent penetration test and staging validation (G-AUDIT) |
| Medium | Trusted-proxy values verified on the real network; edge logs exclude query strings (or are treated as health data) |
| Medium | Failed-email queue (outbox, DB approval) or accept "user retries"; token/code cleanup + audit retention (data deletion approval) |
| Medium | Account enumeration / lockout policy (P3-D17) |
| Low | Least-privilege script for the managed SQL offering (DB approval); platform-admin UI (product) |
| Info | CSP inline styles; dev-only lint advisory; EF include warning; GD-07; `CLAUDE.md` §3 numbering (A3) |

### 21.6 Remaining production blockers (summary)

Blockers: hosting/residency + legal review, email provider, backups/restore, deployment infrastructure. High: MFA key
ring/certificate custody and break-glass, monitoring/alerting/on-call, penetration test. The application itself has no
known open BLOCKER or HIGH defect after Phase 3C; it is **not** production-ready until the external items are done and
verified in staging (gates G-STAGING, G-AUDIT, G-PROD of §18).

---

## Appendix A — Documentation vs code conflicts found

Reported per CLAUDE.md §2; none blocks this plan. Phase 3C resolved A1, A2 and A4 (documentation and comments only, no behaviour change); A3 and A5 stay open.

| # | Document says | Code does | Suggested resolution |
|---|---|---|---|
| A1 | `docs/security/PHASE2-MVP-completion.md` (production review): "patient name search is `contains` without full-text" | `PatientService.ApplySearch` uses **prefix** (`StartsWith`) on name; the code comment says "contains" moves to full-text later | **Resolved (3C):** a dated correction was added to the review line (the report itself is kept as written) |
| A2 | `RateLimiting.cs` comment and `DependencyInjection.cs` comment refer to "Phase 14" for distributed limiting / retry; `DevMailboxEmailSender` comment refers to "phase 9" | `STATUS.md` retired the 15-step numbering; this work is now Phase 3 | **Resolved (3C):** the `RateLimiting.cs` and `DevMailboxEmailSender` comments now cite the Phase 3 tasks (3F, 3D); `DependencyInjection.cs` no longer carries such a comment |
| A3 | `CLAUDE.md` §3 lists the old 15-phase status ("Phase 5 — Frontend… NEXT") | `STATUS.md` says that numbering is retired and is the single status source | Owner to decide whether to update `CLAUDE.md` |
| A4 | `docs/database/schema.md` §4 says TDE "in production (phase 14)" | Same retired numbering | **Resolved (3C):** now "TDE or provider storage encryption in production (plan task 3I; follows the hosting choice)" |
| A5 | `docs/database/schema.md` says the scanner service "is added with the file endpoints (phase 6)" | File endpoints exist; scanner does not; attachments hidden (D3) | Update when 3K is decided |
