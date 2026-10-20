# Deployment prerequisites and configuration reference

> **Status: documentation only.** Nothing has been deployed, no hosting provider has been chosen and no domain, DNS
> record, certificate or secret exists. This page lists what a production-like environment must provide and what the
> application enforces at start-up. It makes **no legal or compliance claim** (PDPL, health-sector, NCA, CST, ZATCA…);
> Saudi legal/compliance review is a separate, external requirement (plan §4, decision 3A-D10).

A *production-like* environment is every environment except `Development` and the automated test host — `Production`,
`Staging`, or any other name, **including "Testing" when the API itself is the executable**. In such an environment the
API refuses to start on unsafe configuration and prints every offending key (never its value).

## 1. Domains, TLS and cookies

| Requirement | Why |
|---|---|
| Web app and API on **sibling subdomains of one registrable domain**: `app.<domain>` and `api.<domain>` (for example `app.example.sa` / `api.example.sa` — placeholders, no real domain is chosen) | The refresh token is a `SameSite=Strict` cookie scoped to the API host. Requests from `app.<domain>` to `api.<domain>` are *same-site*, so the browser sends it. With unrelated domains the cookie would be cross-site and every page reload would sign the user out. Weakening it to `SameSite=None` is not acceptable |
| `<domain>` is a registrable domain, not a public suffix (`example.com.sa`, not `com.sa`) | Same-site is defined by the registrable domain |
| No other subdomain of `<domain>` hosts untrusted or user-generated content | Same-site subdomains can sidestep `SameSite` (the API additionally checks `Origin` on `/auth/refresh` and `/auth/logout`) |
| HTTPS everywhere, certificates valid and renewed | Cookies are `Secure`; HSTS is sent (API: 2 years + subdomains; web app: same) |
| TLS terminated at a trusted proxy/load balancer **or** at the API; if at a proxy, `ForwardedHeaders` must trust it (§6) so the API sees `https` | HTTPS redirection and HSTS depend on the forwarded scheme |
| `Cors:AllowedOrigins` = exactly `https://app.<domain>`; `Auth:AppBaseUrl` = `https://app.<domain>`; web build `NEXT_PUBLIC_API_URL` = `https://api.<domain>` | The guard rejects http, localhost, wildcards, paths, and an `AppBaseUrl` whose origin is not in the CORS list |

The web app bakes `NEXT_PUBLIC_API_URL` into its build (and its CSP `connect-src`), so build the web image **per
environment**.

## 2. Required configuration

All values come from environment variables or a secret store — **never from the repository**. ASP.NET maps `Section:Key`
to `Section__Key`. `appsettings.Production.json` / `appsettings.Staging.json` in the repo only list the keys that must be
supplied (they hold no secrets).

| Key | Required | What the start-up guard enforces |
|---|---|---|
| `Jwt__SigningKey` | yes | ≥ 32 bytes, random; rejected: empty, short, placeholder-looking (`changeme`, `example`, `dev-only`, `test-only`…), too little variety, and the repository's own test keys |
| `Jwt__PreviousSigningKeys__N` | during rotation | same rules; validation only (§4) |
| `Jwt__AccessTokenMinutes` | default 15 | 1–60 |
| `DataProtection__KeyRingPath` | yes | Absolute path of the persisted Data Protection key ring (outside the database). It encrypts the platform admins' TOTP secrets — see [platform-admin-mfa.md](platform-admin-mfa.md) |
| `DataProtection__CertificatePath` + `DataProtection__CertificatePassword`, or `DataProtection__CertificateThumbprint` | yes (one of) | The certificate (private key) that protects the key ring; from the secret store. The API refuses to start if it cannot be loaded; values are never echoed |
| `Auth__MfaLockoutMinutes` | default 60 | Lockout after repeated wrong MFA codes; ≥ 15 |
| `ConnectionStrings__Default` | yes | a real server (not LocalDB); `Encrypt=True` (or Strict); **no** `TrustServerCertificate=True`; not the `sa` login; user **and** password for SQL auth; database name set and not `master` / `ClinicSaaS_Dev*` / `ClinicSaaS_Test*` / `ClinicSaaS_E2E*` |
| `Cors__AllowedOrigins__0` | yes | https origin, no path, no wildcard, no localhost |
| `Auth__AppBaseUrl` | yes | https, not localhost, same origin as one CORS origin |
| `AllowedHosts` | yes | the API host name(s); not empty, not `*` |
| `Email__Provider` | yes | set and not `DevMailbox`. **No production provider exists yet**, so a production-like start is refused by design until one is added (plan §11) |
| `ForwardedHeaders__KnownProxies__N` / `__KnownNetworks__N` **or** `ForwardedHeaders__NoProxy=true` | yes | see §6 |
| `Database__MigrateOnStartup` | must be `false` | production applies reviewed scripts (§7) |
| `RateLimiting__*` | optional | each value 1 … 10× its default (the browser suite's 100000 is refused); more than one API instance is refused (§8) |
| `Auth__MaxFailedAccessAttempts` / `LockoutMinutes` / `RefreshTokenDays` | optional | 3–10 / ≥ 5 / 1–90 |
| `Serilog__MinimumLevel__Default` and `__Override__*` | optional | not Debug/Verbose; `Microsoft*`/`System*` overrides stay at Warning or above (they log full URLs, including query strings) |
| `Bootstrap__SuperAdminEmail`, `Bootstrap__SuperAdminPassword` | first start only | creates the first platform super admin if absent; **remove both afterwards**. While they stay configured after the account exists, every start logs warning event `4104 BootstrapSecretStillConfigured` (never the value). Not enforced |
| `Database__Resilience__*` | optional | connection/read retry tuning (§7) |
| `Observability__ConsoleLogFormat` | `json` in the repo's Staging/Production settings | `json`: one JSON object per line with every property (event id, correlation/user/tenant id, application, environment, version) for the log platform; anything else: readable text. Not a guard |

Not configurable in production by design: the development mailbox, local-disk attachment storage (attachments stay
disabled, decision 3A-D9), OpenAPI/Swagger, detailed error pages.

## 3. Secrets

Production secrets: JWT signing key, SQL credentials (application login and, separately, the migrator login), email
provider credentials, the bootstrap password (once). Rules: separate values per environment; held in the provider's secret
store with access auditing; injected at start-up; never logged (the guard and the log tests check that); backup credentials
are **separate** from application credentials (see `backup-restore.md`).

## 4. JWT signing-key rotation

Access tokens last 15 minutes and sessions do not depend on the signing key (refresh tokens are opaque, stored hashed, and
checked against the database on every request), so rotating the key never signs anyone out of their session: at worst a
client's next call gets a 401 and it refreshes. To make even that invisible, rotate in three steps (each is a normal
configuration change and rolling restart):

1. **Distribute the new key as validation-only.** `Jwt__SigningKey` = *old*, `Jwt__PreviousSigningKeys__0` = *new*. Every
   instance now accepts tokens signed with either key; none signs with the new one yet (so a half-rolled fleet is safe).
2. **Switch signing.** `Jwt__SigningKey` = *new*, `Jwt__PreviousSigningKeys__0` = *old*.
3. **Retire.** After at least `Jwt__AccessTokenMinutes` + 30 s clock skew (use 30 minutes), remove `PreviousSigningKeys`.

**Emergency (key believed leaked):** set `Jwt__SigningKey` to a new value and clear `PreviousSigningKeys` in one step. All
existing access tokens stop validating immediately; clients refresh with their cookies and carry on. A leaked key alone is
not enough to forge a working token — every request is also checked against a live session of that user and clinic — but
rotate anyway and review the audit log. If sessions themselves may be stolen, also revoke them
(`incident-response.md` §9).

Only HS256 is accepted. Automated rotation (a secret-store schedule) is a deployment task, not implemented.

## 5. SQL Server

- SQL Server 2022-compatible; private network only (no public endpoint); encryption in transit (`Encrypt=True` with a
  certificate the API trusts) and at rest.
- Two principals: the **application login** (least privilege — `docs/database/security/least-privilege.sql`, to be adapted
  for the chosen offering) and a **migrator** used only by the deployment pipeline.
- Put `Connect Timeout=8` (or similar) in the connection string. A connection attempt that cannot reach the server waits for
  the full connect timeout, and the API makes up to two attempts (`Database__Resilience__ConnectTries`, default 2), so the
  default 15 s timeout means ~30 s before a caller gets a 503.
- Pool: default ADO.NET pooling (100 per process); size the server's connection limit to `instances × 100` plus headroom.
- Residency, backups and region: decided with the provider (decisions 3A-D1/D2) and legal review.

## 6. Reverse proxy / load balancer and the client address

Rate limiting, lockout diagnostics and the audit log use the client address the framework resolves. The API **never trusts
`X-Forwarded-For` from an arbitrary sender**; configure one of two explicit modes (the guard requires exactly one):

| Mode | Configuration | Behaviour |
|---|---|---|
| Behind proxies | `ForwardedHeaders__KnownProxies__N` (exact IPs) and/or `ForwardedHeaders__KnownNetworks__N` (CIDR, for load balancers whose addresses change inside a subnet — catch-all ranges such as `0.0.0.0/0` are rejected) | `X-Forwarded-For` / `-Proto` are honoured **only** when the TCP peer is listed, and only the entry appended by the closest trusted proxy is used (`ForwardLimit`, default 1; set it to the number of trusted hops in front of the API, max 5). Anything a client wrote in the header is ignored |
| Direct | `ForwardedHeaders__NoProxy=true` | Forwarded headers are not read at all |

What must be supplied later (nothing here is assumed about any provider): the proxy/LB addresses or subnet; whether it
appends to `X-Forwarded-For` (rather than replacing it) and how many hops there are; that it strips/overwrites
client-supplied `X-Forwarded-*` headers; and that the API is **not** reachable around it. After deployment, verify with a
request through the real proxy that the audit log records the caller's address, not the proxy's.

## 7. Migrations and rollback

- Production applies the reviewed idempotent scripts in `docs/database/` with the migrator principal, **before** the new
  application version is deployed; `Database__MigrateOnStartup` is refused in production-like environments. CI keeps the EF
  "no pending model changes" check.
- Expand → migrate → contract; no down-migrations in production. Rollback of the application = redeploy the previous image
  (a migration must therefore stay compatible with the previous version); rollback of data = restore (`backup-restore.md`).
- Take and verify a restore point before every production migration.
- SQL retry (decision 3A-D8): connection opens are retried (default 2 attempts) and plain reads outside a transaction are
  retried (default 3); **writes and transactions are never retried automatically**. Transient failures answer
  `503 TEMPORARILY_UNAVAILABLE` + `Retry-After: 5` with no internal detail; the person decides whether to retry.

## 8. Rate limiting is per instance

The limiter is in the API process's memory. **Run exactly one API instance**; with N instances the effective budget is N
times larger and resets on every restart. The start-up guard refuses `RateLimiting__ApiInstances` > 1 unless
`RateLimiting__AcceptPerInstanceLimits=true` — a deliberate, recorded owner decision to accept per-instance limits (for
example "two instances, limits halved"). Account lockout is stored in the database and *does* work across instances. A
shared store (Redis-compatible) is **not implemented** (decision 3A-D7); if the store were unreachable the design is to
fall back to the in-process limiter, never to fail open on login or password-reset limits. Edge rules (WAF / load balancer)
for anonymous floods are recommended in addition.

## 9. Health endpoints

| Endpoint | Meaning | Use |
|---|---|---|
| `GET /health/live` | The process is up and serving. **Does not touch the database**: `200 Healthy` even when SQL Server is down | Liveness / restart probe, external uptime probe |
| `GET /health/ready` | The API can reach the database. `200 Healthy` or `503 Unhealthy` — the body is exactly that word, never an exception, server or database name | Load-balancer readiness (take the instance out of rotation) |

Both are anonymous, `Cache-Control: no-store`. Expose `/health/ready` only to the load balancer / monitoring if the
platform allows. The web app has no health route of its own beyond serving `/login`.

## 10. Logging, telemetry and monitoring requirements

Application side (implemented and tested): logs go to stdout; request lines carry method, **path without query string**,
status, duration, correlation id, user id, tenant id; framework categories are pinned to Warning in production; no
passwords, access or refresh tokens, one-time tokens, signing keys, request bodies, national IDs or mobile numbers are
logged (`SensitiveLoggingTests`); email-delivery failures log purpose, recipient *domain* and exception *types* only. See
`sensitive-data-in-logs.md` for the full audit.

Monitoring hooks (Phase 3C, implemented and tested): JSON console output in Staging/Production, every line tagged with
application, environment and version (the build's commit), and stable **event ids** for what alerts are built on —
`4100 SecurityAudit` (security trail mirrored from the audit table, identifiers only), `4101 RateLimited`,
`4102 EmailDeliveryFailed`, `4103 TenantIsolationViolation`, `4104 BootstrapSecretStillConfigured`. The alert rules,
thresholds and owners are in [monitoring-and-alerting.md](monitoring-and-alerting.md).

Infrastructure side (**not controllable by the application — must be configured**):

- Reverse-proxy / load-balancer / WAF / CDN access logs must **not record query strings** (patient searches carry mobile
  and national-ID numbers) — or those logs must be handled as personal health data (access control, retention, residency).
- Telemetry agents must not capture full URLs or query strings or request/response bodies.
- Central log store inside the residency boundary chosen by the owner, with role-based access and a retention period
  (proposed 30–90 days), alerts as in plan §10.3 (availability, readiness, 5xx rate, any `Tenant isolation violation`
  log line at Critical, refresh-token reuse spikes, rate-limit spikes, `Database temporarily unavailable` warnings,
  `Email delivery failed` errors, certificate expiry).

## 11. Before the first deployment

The blockers are tracked in `docs/PHASE3-PRODUCTION-PLAN.md` §21.6 (§20.6 for the 3B state). In short: hosting/region and
the Saudi legal review, a production email provider adapter, backups with a tested restore, deployment infrastructure,
the MFA key ring and its certificate, monitoring/alerting with an on-call owner, trusted-proxy configuration verified on
the real network, and an independent security test. The step-by-step list for the infrastructure phase is
[go-live-checklist.md](go-live-checklist.md).
