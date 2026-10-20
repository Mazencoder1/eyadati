# Clinic Management SaaS — Claude Code Instructions

## 1. Project Overview

This project is a Saudi-first, multi-tenant SaaS clinic management platform targeting small and new clinics.

The platform is built around:

- ASP.NET Core 10
- EF Core 10
- SQL Server
- Clean Architecture
- Multi-tenancy
- JWT authentication + refresh token rotation
- RBAC and permissions
- Clinic/branch/user management
- Doctors and working hours
- Services
- Appointments
- SaaS plans and subscription limits
- Saudi localization
- Future billing and Saudi payment integration

The project is NOT production-ready until all planned phases and their verification requirements are completed.

---

# 2. Source of Truth

Before making changes, inspect the existing implementation and documentation.

Important sources:

- `docs/`
- `backend/README.md`
- `backend/src/`
- Database documentation
- Security documentation
- API reference
- `.claude/skills/`
- This `CLAUDE.md`

Do not assume that documentation and implementation are identical.

If documentation conflicts with the actual code:

1. Detect the conflict.
2. Report it.
3. Do not silently choose one.
4. Ask for clarification when the conflict affects architecture, security, database, or API contracts.

---

# 3. Current Project Status

The only phase/status document is `docs/STATUS.md` — read it before starting work, and do not keep phase lists or
progress numbers anywhere else (the former 15-step numbering with percentages is retired).

Phases: **1 — Local core audit (closed)** · **2 — Clinical loop, local MVP (active, gated A–E)** ·
**3+ — Production/Cloud (not started; explicit approval required)**.

---

# 4. Critical Safety Rules

## Database

Never:

- Delete production data.
- Drop tables.
- Drop columns.
- Rename database objects without approval.
- Create migrations without approval.
- Modify schema casually.
- Change constraints or indexes without explaining why.
- Seed destructive data.
- Automatically recreate the database.

Before any database change:

1. Explain the reason.
2. Identify affected tables.
3. Identify affected data.
4. Explain migration impact.
5. Wait for approval when the change is destructive or architectural.

Development database changes must never be assumed to be safe for production.

---

# 5. Multi-Tenancy Rules

Tenant isolation is a core security requirement.

The architecture uses:

- Shared database
- `TenantId`
- Global query filters
- SaveChanges tenant guards
- Composite foreign keys
- Global `User`
- `TenantMembership`

A user may belong to multiple clinics.

Never assume:

```text
User = Tenant
```

Instead:

```text
Platform
  └── Tenant
       ├── Branches
       ├── Users/Memberships
       └── Clinic Resources
```

Every tenant-owned resource must be reviewed for tenant isolation.

Never allow:

- Cross-tenant reads
- Cross-tenant updates
- Cross-tenant deletes
- Cross-tenant references
- Cross-tenant file access
- Cross-tenant appointment access

Whenever adding a new entity, explicitly determine:

- Is it platform-owned?
- Is it tenant-owned?
- Does it require `TenantId`?
- Does it require branch scoping?
- Does it require composite foreign keys?
- Does it need a global query filter?

---

# 6. Branch Isolation

Some clinic resources are branch-scoped.

Branch restrictions must be respected for:

- Appointments
- Doctors
- Working hours
- Services
- Other branch-specific resources

Do not bypass branch authorization simply because the user has tenant-level access.

---

# 7. Security

Security has higher priority than convenience.

Always consider:

- Authentication
- Authorization
- Tenant isolation
- Branch isolation
- RBAC
- Permission checks
- Input validation
- Rate limiting
- Account lockout
- Enumeration protection
- Token rotation
- Refresh-token reuse detection
- Session management
- Security headers
- Audit logging
- Secure file access
- Secret management
- SQL injection
- XSS
- CSRF where applicable
- IDOR / BOLA
- Privilege escalation

Never:

- Hard-code secrets.
- Commit API keys.
- Commit passwords.
- Log passwords or tokens.
- Expose refresh tokens to JavaScript.
- Trust tenant IDs supplied directly by clients.
- Trust user IDs supplied by clients for authorization.

---

# 8. Authentication

Current architecture includes:

- JWT
- Refresh token rotation
- Refresh token reuse detection
- Account lockout
- Password policy
- Email confirmation
- Password reset
- Invitations
- Session management
- Clinic switching

Preserve these security guarantees.

Do not weaken authentication to simplify frontend development.

---

# 9. Authorization

Authorization must be enforced server-side.

Frontend hiding a button is NOT authorization.

Every protected operation must have backend authorization.

The system uses:

- Platform permissions
- Clinic permissions
- Roles
- Custom roles
- Memberships

Never allow users to grant themselves permissions.

Never allow a lower-privileged user to modify protected owner/platform roles.

---

# 10. API Rules

The API should remain consistent.

Follow existing conventions for:

- Authentication
- Authorization
- Validation
- Error responses
- Pagination
- Sorting
- Filtering
- Concurrency
- Localization
- HTTP status codes

Controllers should remain thin.

Business logic belongs in the appropriate application/domain layer.

Do not move business logic into controllers merely because it is faster.

Before changing an API contract:

1. Identify all consumers.
2. Identify breaking changes.
3. Update documentation.
4. Update tests.
5. Explain the migration impact.

---

# 11. Frontend Rules

The current frontend target is:

- Next.js
- App Router
- TypeScript
- Tailwind CSS v4
- RTL-first
- Arabic-first
- English support through `next-intl`
- IBM Plex Sans Arabic

The frontend must consume the existing API rather than duplicating business logic.

Do not recreate authorization logic as a security mechanism in the frontend.

Frontend responsibilities include:

- UI
- User interaction
- Form state
- Client-side validation where useful
- API interaction
- Loading states
- Error states
- Empty states
- Responsive behavior

Backend remains authoritative.

---

# 12. RTL and Saudi-First UI

The primary interface is Arabic.

Use:

```html
dir="rtl"
```

and CSS logical properties.

Prefer:

- `margin-inline`
- `padding-inline`
- `inset-inline`
- `border-start`
- `border-end`

Avoid unnecessary left/right-specific positioning.

The UI must work correctly in:

- Arabic RTL
- English LTR
- Mobile
- Tablet
- Desktop
- Large screens

Use `Asia/Riyadh` for Saudi-local time behavior where appropriate.

Do not implicitly convert dates to Hijri unless explicitly required.

---

# 13. UI / UX Quality

The interface must look like a professionally designed healthcare SaaS product.

Avoid:

- AI-generated visual patterns
- Excessive gradients
- Excessive rounded cards
- Unnecessary glassmorphism
- Excessive shadows
- Huge decorative headings
- Random icons
- Excessive animations
- Overloaded dashboards
- Generic template-looking layouts
- Unnecessary badges
- Visual noise

Prioritize:

- Clear hierarchy
- Strong typography
- Consistent spacing
- Accessible contrast
- Calm healthcare-oriented visual language
- Efficient workflows
- Predictable navigation
- Good empty states
- Useful feedback
- Consistent components

Use the `frontend-design` skill when designing or improving UI.

Do not change existing functionality merely to make the UI visually different.

---

# 14. Design System

The frontend should use reusable design tokens and components.

Expected shared components include:

- Button
- Input
- Select
- Card
- Table
- Modal
- Toast
- Badge
- Tabs
- Pagination
- Breadcrumbs
- EmptyState
- Loading states
- Error states

Do not create multiple slightly different implementations of the same UI component.

Before creating a new component, check whether an existing shared component can be reused.

---

# 15. API Client and Tokens

The frontend should follow the planned token architecture:

- Access token in memory
- Refresh token through secure cookie mechanism

Do not place sensitive long-lived authentication tokens in localStorage unless explicitly approved.

Centralize API communication.

Do not scatter raw `fetch()` implementations throughout the application.

Handle:

- Authentication errors
- Validation errors
- Authorization errors
- Rate limiting
- Network failures
- Server errors

consistently.

---

# 16. Clinic Domain

The current clinic domain includes:

- Clinics
- Branches
- Users
- Memberships
- Roles
- Permissions
- Doctors
- Working hours
- Services
- Appointments
- Patients
- Subscription plans

Appointments currently support:

```text
Scheduled
Confirmed
CheckedIn
Completed
Cancelled
NoShow
```

Appointment rules include:

- Clinic timezone
- Doctor working hours
- Conflict prevention
- Rescheduling
- Cancellation reasons
- No-show restrictions
- Walk-in queue numbers
- Availability calculation

Do not bypass these rules from the frontend.

---

# 17. Appointment Concurrency

Double-booking prevention is security/business-critical.

The system uses:

- `sp_getapplock`
- Filtered unique index
- Concurrency tests
- Mutation testing

Do not remove or weaken these protections.

Any change to appointment booking must include concurrency testing.

---

# 18. SaaS Plans

Plans and limits are data-driven.

Do not hard-code subscription limits into random controllers or frontend components.

Subscription rules currently cover resources such as:

- Users
- Doctors
- Patients
- Branches
- Monthly appointments
- Features

When adding a new subscription-limited feature:

1. Determine whether it is a limit or feature.
2. Keep enforcement server-side.
3. Return consistent upgrade-required responses.
4. Ensure the frontend can display the restriction.

---

# 19. Saudi Localization

The platform targets Saudi Arabia first.

Consider:

- Arabic
- English
- SAR
- `Asia/Riyadh`
- Saudi mobile numbers
- Saudi national ID validation where applicable
- Commercial registration where applicable
- VAT
- Saudi payment methods
- Saudi SMS providers
- Saudi regulatory requirements

Do not claim compliance with:

- PDPL
- NCA
- ZATCA
- Ministry of Health requirements

unless formally verified.

---

# 20. Testing

The existing test suite is important.

Current test count is approximately:

- 64 unit tests
- 76 integration tests
- 140 total (backend, verified on `integration` 6316dca, 2026-10-06)

Frontend tests (vitest) exist but have not been run on the current dev machine (Node.js not installed).

Tests must not be deleted merely because they are inconvenient.

After significant changes:

1. Run relevant tests.
2. Run the full test suite when practical.
3. Investigate failures.
4. Do not hide failures by weakening assertions.
5. Do not claim success without verification.

For security-sensitive changes, add regression tests.

For appointment changes, include concurrency tests.

For tenant-sensitive changes, include cross-tenant isolation tests.

---

# 21. Frontend Testing

Frontend work must eventually verify:

- Authentication flows
- Login
- Registration
- Password reset
- Invitation acceptance
- Clinic switching
- Permissions
- Appointment booking
- Appointment rescheduling
- Patient workflows
- Doctor workflows
- Responsive behavior
- RTL
- Error states
- Empty states
- Loading states

Use the `webapp-testing` skill where appropriate.

---

# 22. Performance

Do not optimize blindly.

When performance work begins:

- Measure first.
- Identify the bottleneck.
- Inspect SQL queries.
- Inspect execution plans.
- Review indexes.
- Review N+1 queries.
- Review API payload size.
- Review frontend rendering.
- Review caching opportunities.

Do not add Redis or other infrastructure simply because it is popular.

Every infrastructure dependency must have a justified purpose.

---

# 23. Dependencies

Do not add a dependency unless:

1. The problem genuinely requires it.
2. Existing functionality cannot reasonably solve it.
3. The dependency is maintained.
4. Security implications are understood.
5. Bundle/runtime impact is understood.
6. It fits the architecture.

Avoid dependency bloat.

---

# 24. File Attachments

Patient/clinic attachments are private data.

Never expose private files through publicly accessible URLs without appropriate authorization.

Verify:

- Tenant ownership
- Branch access where applicable
- User permissions
- File type
- File size
- Secure storage
- Download authorization

---

# 25. Logging and Monitoring

Current monitoring foundation includes:

- Structured logging
- Correlation ID
- Health checks

Never log:

- Passwords
- Access tokens
- Refresh tokens
- API keys
- Sensitive patient information unnecessarily

Use correlation IDs for troubleshooting.

---

# 26. Production Readiness

The system must not be described as production-ready until the planned phases and required verification are complete.

Before production:

- Security audit
- Tenant isolation verification
- Authentication verification
- Authorization verification
- Database backup/restore verification
- Migration verification
- Performance testing
- Monitoring
- Logging
- Error handling
- Secrets management
- HTTPS
- Rate limiting
- Background jobs
- Payment integration
- Notification integration
- Deployment validation
- Rollback plan

---

# 27. Development Workflow

For every meaningful task:

## Step 1 — Understand

Inspect the relevant existing code.

## Step 2 — Plan

Explain:

- What will change
- Why
- Which files
- Which layers
- Risks
- Tests required

## Step 3 — Implement

Make the smallest reasonable change.

## Step 4 — Verify

Run relevant tests/build/lint/type checks.

## Step 5 — Review

Check:

- Security
- Tenant isolation
- Performance
- Maintainability
- UI consistency
- Regression risk

## Step 6 — Report

Clearly state:

- What changed
- What was tested
- What passed
- What remains
- Any known risks

Never claim verification that was not actually performed.

---

# 28. Change Discipline

Prefer small, reversible changes.

Do not:

- Rewrite entire modules unnecessarily.
- Refactor unrelated code while implementing a feature.
- Rename large portions of the codebase without reason.
- Modify working backend systems while implementing frontend features.
- Mix multiple phases into one task.

If a task reveals an unrelated problem, report it separately unless it blocks the current task.

---

# 29. Current Priority

The current phase, gate and approved scope are in `docs/STATUS.md`. Work only inside the approved gate, stop at the
end of each gate, and do not start production deployment, cloud infrastructure, billing or external integrations
unless explicitly approved.

---

# 30. Claude Behavior

You are a senior software engineer working on an existing production-oriented SaaS architecture.

Do not behave like a code generator.

Before changing important code:

- Understand it.
- Check existing patterns.
- Reuse existing abstractions.
- Protect existing behavior.
- Consider security.
- Consider tenant isolation.
- Consider backwards compatibility.
- Test the change.

When uncertain, investigate instead of guessing.

When a decision affects architecture, database, security, API contracts, or production infrastructure, explain the decision before implementing it.

Do not make destructive changes without explicit approval.

The goal is not simply to make the code compile.

The goal is to build a secure, maintainable, scalable, Saudi-first multi-tenant clinic SaaS platform.

---

# 31. Parallel Agents (Git Worktrees)

This repository is developed by parallel agents, each in its own git worktree and branch. Read `docs/agents/PARALLEL-WORKFLOW.md`.

- Check your branch first (`git branch --show-current`). Never work on `main`.
- Stay inside your worktree and your scope (enforced by `scripts/agents/hooks/pre-commit`).
- Only `agent/database` creates or edits migrations. No agent applies migrations to any non-test database.
- Run the app via `scripts/agents/run-api.ps1` / `run-web.ps1` and verify via `scripts/agents/verify.ps1`.
- Commit on your own branch. Only the Manager merges into `integration`, and only `promote-to-main.ps1` updates `main`.
