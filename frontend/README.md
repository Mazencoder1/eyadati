# Clinic SaaS — Frontend

Next.js 16 (App Router) · TypeScript · Tailwind v4 · next-intl (Arabic default, English) · RTL-first.

```bash
npm ci
npm run dev         # http://localhost:3000 — talks to the API at NEXT_PUBLIC_API_URL (default http://localhost:5056)
npm run lint
npm run typecheck
npm test            # vitest
npm run build
```

- `src/app/` routes · `src/features/` screens by domain · `src/components/` shared UI · `src/lib/` API client, auth
  session, date/locale helpers · `messages/` ar/en catalogs (same keys, enforced by a test).
- The UI hides what a user may not do, but the API enforces every permission; never move authorization to the client.

Project overview, architecture and status: [../README.md](../README.md) · [../docs/STATUS.md](../docs/STATUS.md).
