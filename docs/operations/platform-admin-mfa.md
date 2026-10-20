# Platform-admin MFA: operations

> **Status: implemented in code, nothing deployed.** This page is what an operator needs to run platform-admin MFA
> (TOTP + one-time recovery codes) in a production-like environment. It makes **no compliance claim**. The design and its
> threat model are in `docs/PHASE3-PRODUCTION-PLAN.md` §19.5.

MFA applies to every account with `PlatformRole != None` (the bootstrap super admin today). Clinic users are not affected.
Email is never a factor: a mailbox can reset a password but cannot satisfy or reset MFA.

## 1. Production prerequisite: the Data Protection key ring

The TOTP secrets are stored encrypted with ASP.NET Core Data Protection. The **key ring** that decrypts them must be:

| Requirement | Why |
|---|---|
| **Persisted outside the SQL database** (a directory on durable storage that survives restarts and redeploys) | A key ring kept in the database next to the encrypted secrets would give a database thief both halves |
| **Protected by a key that is not in the database either**: an X.509 certificate with a private key, supplied from the secret store | The ring's own keys are encrypted at rest with it |
| **Backed up separately from the database** | A restored database without the key ring cannot decrypt a single secret: every platform admin would have to be reset by break-glass |
| **The same ring on every API instance** (shared directory) | Exactly one API instance is supported today (plan §20); this matters when that changes |

| Key | Required | Meaning |
|---|---|---|
| `DataProtection__KeyRingPath` | yes | Absolute path of the persisted key ring |
| `DataProtection__CertificatePath` + `DataProtection__CertificatePassword` | one of these two | A PKCS#12 file holding the protection certificate, and its password (from the secret store) |
| `DataProtection__CertificateThumbprint` | one of these two | A certificate with a private key in the machine or user certificate store |
| `Auth__MfaLockoutMinutes` | default 60 | Lockout after `Auth__MaxFailedAccessAttempts` wrong MFA codes; the guard rejects < 15 |

A production-like environment **does not start** without a key ring path and a loadable certificate; the error names the
setting, never its value. There is no fall-back to an ephemeral or default location outside Development.

**Not decided:** hosting is not chosen, so no cloud KMS adapter exists. A KMS-protected ring (instead of a certificate)
is a later swap in `MfaDataProtection`; nothing else changes. **Rotation:** Data Protection rolls its keys
automatically and keeps the old ones, so existing secrets stay readable. A *compromised* ring or certificate means every
platform admin must be reset (section 3) and the ring replaced.

If the ring is lost or unreadable the API fails closed: platform sign-in answers "invalid code" (and logs an error with the
user id, never a secret). Recovery codes still work (they are hashed, not encrypted), so an admin can sign in with one and
re-enrol.

## 2. Admin-facing behaviour

- Password correct for a platform account: **no session is created**. The API answers `409 MFA_REQUIRED` (authenticator
  exists) or `409 MFA_ENROLLMENT_REQUIRED` (it must be set up first) with a short-lived token (5 / 10 minutes).
- Enrolment: setup key (and an `otpauth://` link) shown once, the admin enters a code, then **10 recovery codes** are shown
  once. All earlier sessions of the account are ended.
- A wrong code counts toward the account lockout (`Auth__MaxFailedAccessAttempts`, then `Auth__MfaLockoutMinutes`).
  Only a completed MFA step resets the counter.
- **Recovery codes** work once each. Regenerating them needs a current authenticator code and replaces all ten.
- **Removing MFA** (current password + a code) deletes the factor and the codes and ends every session, including the
  caller's. The next sign-in must enrol again.
- A platform account without an authenticator holds no valid session, even an old one.
- Keep the recovery codes offline, separately from the password. Keep at least two platform admins once platform-user
  management exists (it does not yet), so one lost device never blocks the platform.

## 3. Break-glass: an admin lost the authenticator AND the recovery codes

There is **deliberately no API** for this: no support role, and no other admin, can reset someone's MFA through the
application. It is an operator action on the database, and it must be:

1. **Verified out of band.** Confirm the person's identity by a channel that is not the account's mailbox (in person, or a
   call to a number known beforehand). Record how.
2. **Approved by two people** (the requester's owner/manager and a second operator). Record both names and a ticket id.
3. **Executed once, in a transaction, by the script below**, which also writes the `auth.mfa.reset` audit event. Run it
   against the production database only through the approved access path (never from a laptop with standing access).
4. **Followed by fresh enrolment.** The reset ends every session; the admin signs in with the password, and is taken
   through enrolment with a new authenticator and new recovery codes. Check `auth.mfa.enabled` appears in the audit log.
5. **Reviewed afterwards**: an `auth.mfa.reset` row with no ticket reference in `Details` is an incident (see
   `incident-response.md`).

```sql
-- Replace the three placeholders. Run as one transaction. Nothing here touches Users or passwords.
BEGIN TRANSACTION;

DECLARE @userId uniqueidentifier = '00000000-0000-0000-0000-000000000000';          -- the admin
DECLARE @ticket nvarchar(100)    = N'TICKET-ID';                                    -- the approval record
DECLARE @approvers nvarchar(200) = N'name1, name2';

IF NOT EXISTS (SELECT 1 FROM [Users] WHERE [Id] = @userId AND [PlatformRole] <> 'None')
    THROW 50001, 'Not a platform account', 1;

DELETE FROM [UserRecoveryCodes] WHERE [UserId] = @userId;
DELETE FROM [UserTotpFactors]   WHERE [UserId] = @userId;
UPDATE [RefreshTokens]
   SET [RevokedAtUtc] = SYSUTCDATETIME(), [RevokedReason] = 'mfa_reset'
 WHERE [UserId] = @userId AND [RevokedAtUtc] IS NULL;

INSERT INTO [AuditLogs] ([UserId], [OccurredAtUtc], [Action], [EntityType], [EntityId], [Result], [Details])
VALUES (@userId, SYSUTCDATETIME(), 'auth.mfa.reset', 'User', CONVERT(varchar(64), @userId), N'Success',
        N'{"ticket":"' + @ticket + N'","approvers":"' + @approvers + N'"}');

COMMIT TRANSACTION;
```

Without a factor row the account is back to "must enrol": its password alone still creates no session.

## 4. What is logged and audited

Audit actions: `auth.mfa.challenge_issued`, `auth.mfa.enrollment_started`, `auth.mfa.enrollment_failed`, `auth.mfa.enabled`,
`auth.mfa.challenge_failed` (kind `totp` / `recovery`, reason `wrong_code` / `replay`), `auth.mfa.challenge_succeeded`,
`auth.mfa.recovery_code_used` (remaining count), `auth.mfa.recovery_codes_regenerated`, `auth.mfa.disabled` and
`auth.mfa.reset` (written by the runbook above only), plus the existing `auth.login.succeeded` and `auth.account.locked`.
Secrets, codes, recovery codes, the setup URI, tokens and passwords are never logged or audited.

**Alerting:** the API emits these events as log event 4100 (identifiers only) and the proposed rules A2–A4 and A9 are in
[monitoring-and-alerting.md](monitoring-and-alerting.md); **no alert is live yet** (no log store or on-call exists). Until one is, review `auth.mfa.challenge_failed`,
`auth.account.locked` and `auth.mfa.recovery_code_used` for platform accounts by hand. Roughly 1% a month is the chance a
sustained guesser who already knows the password hits a valid 6-digit code at 5 guesses per hour.
