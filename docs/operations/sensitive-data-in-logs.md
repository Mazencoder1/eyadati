# Sensitive data in logs, URLs and telemetry — audit

Phase 3B audit of where one-time tokens, credentials and personal data can be written down, what the application controls,
and what only the infrastructure can. Method: read every log statement and logging configuration, every place a token is
created, sent or accepted, and every request that carries personal data in a URL; then added tests that capture log output
and the browser's request URLs. **No legal or compliance claim is made.**

## 1. One-time tokens (password reset, email verification, invitation)

| Where a token travels | Before Phase 3B | Now | Controlled by |
|---|---|---|---|
| The emailed link | `/reset-password?token=…` (query string) | `/reset-password#token=…` (**fragment**) | application |
| Request to the web server / reverse proxy / load balancer / WAF / CDN | Full URL with token → in every access log on the path | The browser **never sends a fragment**: only `/reset-password` is requested | application (link format); proxy logs of *legacy* links, below |
| Request to the API | Always a POST **body** (`{ token, … }`); the API does not read it from a URL (a query-string token is ignored) | unchanged — test `The_api_reads_the_token_only_from_the_request_body_never_from_the_url` | application |
| Browser address bar / history / "copy link" | Visible | Read once, then removed from the address bar (`history.replaceState`); kept in memory only | application |
| `Referer` header | Same-origin navigation could carry the query string | `Referrer-Policy: no-referrer` and `Cache-Control: no-store` on the three pages; a fragment is never in a `Referer` anyway | application |
| Application logs | Not logged | Not logged — request log has the path only (no query string); error handler logs no body; email failure logs have purpose, recipient domain and exception types only | application, **tested** (`SensitiveLoggingTests`, `ResilienceAndHealthTests`) |
| Exception text | Tokens are hashed (SHA-256) before lookup; exception messages do not contain them | unchanged | application |
| Audit log (database) | Event type and ids only | unchanged | application |
| Database | Only the **hash** is stored; single use; newest only; expiries 30 min / 48 h / 7 days | unchanged | application |
| Dev mailbox files | Contain full links | Development only; the API refuses to start with it elsewhere | application |
| The email itself (provider, recipient's mail server and mailbox) | Contains the link | Contains the link | **provider / recipient — cannot be controlled**; the token is single-use and short-lived |
| **Links emailed before this change** (`?token=`) | n/a | Still accepted by the web app (read, then stripped) until they expire — at most 7 days for invitations | proxy/web-server access logs of those requests: **infrastructure**. No production email has ever been sent, so no such link exists in production |

Residual: a user who *copies the link from the address bar before the page strips it* or forwards the email forwards a
single-use, short-lived token; nothing more is possible without changing how email links work.

## 2. Credentials and sessions

| Item | Logged? | Notes |
|---|---|---|
| Passwords | No | Never leave the request body; tested |
| Access token (`Authorization` header) | No | Request logging records no headers; tested |
| Refresh token (cookie / native body) | No | Tested; browsers never receive it in a body (Phase 3A) |
| JWT signing key, connection string, email credentials | No | The configuration guard names the offending **key**, never the value; tested |
| Request and response bodies | No | Tested with a request body containing a secret on a server-error path |

## 3. Personal data in URLs — a finding

Not a token, but the same class of exposure in the same logs:

| Endpoint | Query string carries |
|---|---|
| `GET /api/v1/patients?search=` | a name, mobile number, file number or **national ID** |
| `GET /api/v1/patients/duplicates?mobile=&identityNumber=` | a mobile number and a **national ID** |
| `GET /api/v1/audit-logs?…`, appointment/visit/doctor lists | user ids, dates, patient ids (identifiers rather than names) |

What the application does: nothing in the application writes these query strings to its logs — the request log has the
path only, and the guard forbids lowering framework log levels (at Information the framework logs the full URL, which a test
shows would capture the national ID). What it cannot do: **reverse-proxy, load-balancer, WAF, CDN and APM access logs record
the full URL by default.** Changing the API (moving these searches to POST) would be a breaking contract change and is not
part of Phase 3B; treat it as a later decision. Until then it is an **infrastructure requirement** (below).

Severity: MEDIUM — mitigable entirely by configuration, but easy to miss.

## 4. Infrastructure requirements (cannot be enforced by the application)

1. Reverse proxy / load balancer / WAF / CDN access logs: **do not log query strings**, or log them to a store that is handled
   as personal health data (restricted access, retention, residency).
2. Telemetry/APM agents: disable capture of full URLs, query strings, headers and request/response bodies for the API and the
   web app.
3. Do not enable verbose logging on the web server (Next.js) or the proxy in production.
4. Central log store: inside the residency boundary the owner chooses, role-based access, retention period set deliberately.
5. Forward the application's correlation id (`X-Correlation-Id`) through the proxy so an incident can be traced without URLs.

## 5. How this is kept true

- `SensitiveLoggingTests.Nothing_sensitive_reaches_the_logs_across_the_account_lifecycle` runs registration, sign-in (web and
  native), failed sign-in, refresh, reset, verification, invitation, patient creation and search, password change and logout
  with the application logging at the most verbose level and fails if any password, token, key, national ID or mobile number
  appears in any captured event. Raising the framework log level makes it fail (verified), which is why the start-up guard
  forbids that setting.
- `OneTimeLinkTests` pins the emailed link format; `frontend/e2e/one-time-links.spec.ts` proves in a real browser that no
  request URL contains the token and the address bar is cleaned.
- Phase 3C monitoring events (monitoring-and-alerting.md): the security trail mirrored to the log (event 4100) carries
  identifiers only — action, result, audit row id, entity type/id (always GUIDs), user id, tenant id — never the audit
  details, client IP, user agent, email or mobile number, and clinical activity is not mirrored at all; rate-limit refusals
  (4101) log the path without its query string and no client address. `MonitoringEventsTests` checks both (an email, a
  mobile number and a patient id are searched for in every captured event).
