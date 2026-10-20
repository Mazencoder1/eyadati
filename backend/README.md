# ClinicSaaS — Backend

ASP.NET Core 10 · EF Core 10 · SQL Server · Clean Architecture

```text
src/
  ClinicSaaS.Domain          entities, invariants, permission catalog, role templates, Saudi formats
  ClinicSaaS.Application     use cases (AuthService, PatientService…), validation, SaaS gates, interfaces
  ClinicSaaS.Infrastructure  AppDbContext (tenant filters + save guard), EF configurations, migrations, JWT, hashing
  ClinicSaaS.Api             thin controllers, unified errors, permission policies, rate limiting, health checks
tests/
  ClinicSaaS.UnitTests         pure business rules
  ClinicSaaS.IntegrationTests  real API + real SQL Server, throw-away database per run
```

## Prerequisites

- .NET SDK 10
- SQL Server: LocalDB (`(localdb)\MSSQLLocalDB`, Windows) or SQL Server 2022 in a container (see the root README)
- `dotnet tool install -g dotnet-ef`

## Run

```bash
dotnet run --project src/ClinicSaaS.Api --launch-profile http     # http://localhost:5056
```

- Development migrates the database on startup.
- API reference: `docs/05-api-reference.md`; OpenAPI document (Development only): `GET /openapi/v1.json`
- Health: `GET /health/live`, `GET /health/ready`
- Development emails are written to `src/ClinicSaaS.Api/.dev-mailbox/*.html` (git-ignored), because
  `appsettings.Development.json` sets `Email:Provider` to `DevMailbox`. That provider is refused in every
  environment except Development (also in Testing — the automated tests replace the sender), and a missing provider is
  refused outside Development/Testing: the API will not start in Staging/Production until a real email provider is
  added (Production/Cloud phase).
- In Development the JWT key is generated at startup (sessions end on restart).

## Configuration (non-Development)

Every environment except `Development` (and the automated test host) is **production-like**: the API checks its
configuration at start-up and **refuses to start**, listing every unsafe *key* (never its value). Supply values via
environment variables / a secret store — never commit them. Full reference and rationale:
[`docs/operations/deployment-prerequisites.md`](../docs/operations/deployment-prerequisites.md).

| Key | Notes |
|---|---|
| `ConnectionStrings__Default` | least-privilege login (`docs/database/security/least-privilege.sql`); must name a real server, `Encrypt=True`, no `TrustServerCertificate=True`, not `sa`, not a dev/test database |
| `Jwt__SigningKey` | ≥ 32 random bytes; placeholders, short or low-variety keys and the repository's test keys are rejected |
| `Jwt__PreviousSigningKeys__0` | only during a key rotation (validation only) |
| `Cors__AllowedOrigins__0` | the web app origin, `https://app.<domain>` (https, no path/wildcard/localhost) |
| `Auth__AppBaseUrl` | the web app's public https URL (same origin as the CORS entry); emailed links are built from it |
| `AllowedHosts` | the API host name(s) — not empty, not `*` |
| `ForwardedHeaders__KnownProxies__N` / `__KnownNetworks__N` (CIDR), or `ForwardedHeaders__NoProxy=true` | **required**; forwarded headers are trusted only from these peers, so clients cannot choose their address |
| `Email__Provider` | required; `DevMailbox` is refused; no production provider exists yet, so a production-like start is refused |
| `Bootstrap__SuperAdminEmail`, `Bootstrap__SuperAdminPassword` | creates the first platform super admin on startup if absent; remove afterwards (while they stay set, each start logs warning event 4104) |
| `DataProtection__KeyRingPath` + `DataProtection__CertificatePath`/`__CertificatePassword` or `__CertificateThumbprint` | **required**: persisted key ring outside the database and the certificate protecting it; they encrypt the platform admins' MFA secrets ([`platform-admin-mfa.md`](../docs/operations/platform-admin-mfa.md)) |
| `Auth__MfaLockoutMinutes` | optional (default 60, at least 15) |
| `Observability__ConsoleLogFormat` | `json` in the repository's Staging/Production settings: one JSON object per log line with stable event ids for alerting ([`monitoring-and-alerting.md`](../docs/operations/monitoring-and-alerting.md)) |
| `RateLimiting__*` | optional; values above 10× the default are refused; one API instance only (limits are per process) |
| `Saas__DefaultPlanCode` | plan a self-registered clinic starts on (default `basic`, as a trial) |
| `Saas__CatalogHiddenPlanCodes__0` | plan codes left out of the public catalog (default `free`) |

`Database:MigrateOnStartup` is `true` only in Development/tests and refused in every production-like environment —
production applies the reviewed idempotent scripts in `docs/database/`. `appsettings.Production.json` /
`appsettings.Staging.json` list the keys that must be supplied and contain no secrets.

## Tests

```bash
dotnet test
```

Integration tests create `ClinicSaaS_Test_<guid>`, migrate it, and drop it afterwards. They use LocalDB unless
`CLINICSAAS_TEST_SQL_SERVER` holds a server connection string (no database name), e.g.
`Server=localhost,1433;User ID=sa;Password=…;TrustServerCertificate=True`.

## Adding a migration

Follow `docs/database/schema-changelog.md` (impact → justification → migration → tests), then:

```bash
dotnet ef migrations add <Name> -p src/ClinicSaaS.Infrastructure -s src/ClinicSaaS.Api -o Persistence/Migrations
dotnet ef migrations script --idempotent -p src/ClinicSaaS.Infrastructure -s src/ClinicSaaS.Api -o ../docs/database/<NNN>_<Name>.sql
```
