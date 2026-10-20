# Operations

What running Clinic SaaS in production will require. **None of this has been deployed or exercised** — the product is not
production-ready, and nothing here is a compliance claim.

| Document | Status |
|---|---|
| [deployment-prerequisites.md](deployment-prerequisites.md) — domains, TLS, required configuration and what the API refuses to start without, JWT key rotation, SQL, proxy trust, migrations, health endpoints | Requirements written; start-up guards **implemented and tested**; infrastructure **not provisioned** |
| [platform-admin-mfa.md](platform-admin-mfa.md) — key-ring prerequisite, admin behaviour, break-glass reset, audit events | MFA **implemented and tested**; key ring, break-glass procedure and alerting **not provisioned** |
| [backup-restore.md](backup-restore.md) | **NOT IMPLEMENTED — OPERATIONAL REQUIREMENT** |
| [incident-response.md](incident-response.md) | Runbook written; **not yet exercised**; legal notification duties **require Saudi legal/compliance review** |
| [sensitive-data-in-logs.md](sensitive-data-in-logs.md) | Audit done; application side **implemented and tested**; infrastructure requirements listed |
| [monitoring-and-alerting.md](monitoring-and-alerting.md) — log event ids, JSON log format, proposed alert rules A1–A17 and how to test them | Application hooks **implemented and tested** (Phase 3C); provider, alerts and on-call **not provisioned** |
| [go-live-checklist.md](go-live-checklist.md) — every infrastructure item (what, why, where, who, how verified, pre-go-live test) and the owner's go-live sequence | Checklist; **nothing provisioned** |

The plan these belong to is [`../PHASE3-PRODUCTION-PLAN.md`](../PHASE3-PRODUCTION-PLAN.md) (§20 records Phase 3B, §21
Phase 3C); the single status page is [`../STATUS.md`](../STATUS.md).
