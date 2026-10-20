# Core browser regression suite

A small Playwright suite for the MVP's critical paths. It drives a **running local stack** in a real browser (Chromium)
and builds its own test clinics through the public API — it never touches the database directly.

| Spec | Covers |
|---|---|
| `owner.spec.ts` | sign-in and navigation, staff invitation, services and branches, doctors, subscription state, role editor, audit log |
| `reception.spec.ts` | patient registration, booking, rescheduling, cancellation, walk-in, check-in |
| `doctor.spec.ts` | Today queue → own visit, medical alerts, clinical data, prescription + print view, completion, history |
| `security.spec.ts` | cross-tenant 404s, branch restriction, receptionist vs clinical records, treating-doctor rules, disabled member |
| `account.spec.ts` | change password (wrong current password, success), signed-in devices, ending a session (its access token dies at once) |
| `mfa.spec.ts` | platform-admin two-step verification: enrolment (setup key, recovery codes shown once), the sign-in challenge with a TOTP and with a recovery code, replacing the recovery codes, removing MFA; axe in Arabic and English |
| `sessions.spec.ts` | logout, password change and suspension/reactivation end the access token immediately (GD-03, GD-15) |
| `refresh-token.spec.ts` | normal refresh uses the httpOnly cookie only; in-page script can't obtain a refresh token, even asking for native mode; four tabs loading at the same moment keep the session (P3-SEC-25) |
| `layout.spec.ts` | Arabic RTL / English LTR × 375, 768, 1024, 1440 px: one `h1`, no horizontal overflow, axe WCAG 2.2 AA |
| `accessibility.spec.ts` | every main dialog (axe, focus in, Esc, focus back), skip link, mobile navigation, first invalid field, language switch |

## Running it locally

1. **API** (Development, its own throw-away database recommended), with:
   - a local super admin (used only to move the test clinic to the "professional" plan through the platform API);
   - higher auth / sensitive rate limits — the suite signs in and accepts invitations many times from one IP.

   ```bash
   cd backend
   export ConnectionStrings__Default='Server=(localdb)\MSSQLLocalDB;Database=ClinicSaaS_E2E;Trusted_Connection=True;TrustServerCertificate=True'
   export Bootstrap__SuperAdminEmail=platform.e2e@example.com
   export Bootstrap__SuperAdminPassword='<local-only password>'
   export RateLimiting__AuthPerMinute=1000 RateLimiting__SensitivePer15Minutes=1000
   dotnet run --project src/ClinicSaaS.Api --launch-profile http
   ```

   These are configuration values only; never use them outside a local test run.

2. **Web app** (production build is closest to real use):

   ```bash
   cd frontend
   npm run build && npm run start
   ```

3. **Suite** (first time: `npx playwright install chromium`):

   ```bash
   cd frontend
   export E2E_PLATFORM_EMAIL=platform.e2e@example.com E2E_PLATFORM_PASSWORD='<same local-only password>'
   npm run test:e2e
   ```

**Platform admin and MFA.** The bootstrap super admin must use two-step verification, so the suite signs it in through the real
endpoints (`platformLogin()` in `support/api.ts`): it enrols the account on first use and remembers the authenticator secret of
that *local* database in `frontend/test-results/.platform-mfa.json` (git-ignored). Use a fresh local database for a new run; if the
admin already has an authenticator the suite does not know, set `E2E_PLATFORM_TOTP_SECRET` (the base32 setup key). A code can be
used once per 30-second step, so the suite occasionally waits for the clock. `mfa.spec.ts` takes the account back to "no
authenticator" first and leaves it enrolled.


Optional: `E2E_API_URL` (default `http://localhost:5056`), `E2E_WEB_URL` (default `http://localhost:3000`),
`E2E_MAILBOX_DIR` (default `backend/src/ClinicSaaS.Api/.dev-mailbox`, where Development writes invitation emails).

Each run creates new clinics with unique names; nothing is deleted. Use a dedicated local database for it.
