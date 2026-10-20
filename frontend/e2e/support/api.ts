import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

/** Where the suite finds the running app. Defaults match the local development setup (README). */
export const env = {
  apiUrl: (process.env.E2E_API_URL ?? "http://localhost:5056").replace(/\/+$/, ""),
  webUrl: (process.env.E2E_WEB_URL ?? "http://localhost:3000").replace(/\/+$/, ""),
  mailbox: process.env.E2E_MAILBOX_DIR ?? path.resolve(__dirname, "../../../backend/src/ClinicSaaS.Api/.dev-mailbox"),
  platformEmail: process.env.E2E_PLATFORM_EMAIL ?? "",
  platformPassword: process.env.E2E_PLATFORM_PASSWORD ?? "",
};

export const PASSWORD = "E2e-Password-2026";

export class ApiCallError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
  ) {
    super(message);
  }
}

/** Minimal JSON client for test setup and API-level security checks. */
export async function call<T = unknown>(method: string, url: string, token?: string | null, body?: unknown): Promise<T> {
  const res = await fetch(`${env.apiUrl}/api/v1${url}`, {
    method,
    headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  let json: { data?: unknown; code?: string; errors?: unknown } | null = null;
  try {
    json = await res.json();
  } catch {
    // 204 or empty body
  }
  if (res.status >= 400) {
    throw new ApiCallError(res.status, json?.code ?? "", `${method} ${url} → ${res.status} ${json?.code ?? ""} ${JSON.stringify(json?.errors ?? "")}`);
  }
  return json?.data as T;
}

/** HTTP status of a call that is expected to fail (or succeed) — for authorization checks. */
export async function statusOf(method: string, url: string, token: string, body?: unknown): Promise<{ status: number; code: string }> {
  try {
    await call(method, url, token, body);
    return { status: 200, code: "" };
  } catch (e) {
    if (e instanceof ApiCallError) return { status: e.status, code: e.code };
    throw e;
  }
}

// ------------------------------------------------------------------ data helpers

let counter = 0;
export const unique = (prefix: string) => `${prefix}.${Date.now().toString(36)}${(counter++).toString(36)}`;

export const mobile = () => "05" + String(Math.floor(10000000 + Math.random() * 89999999));

/** A valid Saudi national ID (starts with 1, Luhn check digit). */
export function nationalId(): string {
  const d = [1, ...Array.from({ length: 8 }, () => Math.floor(Math.random() * 10))];
  let sum = 0;
  d.forEach((x, i) => {
    if (i % 2 === 0) {
      const y = x * 2;
      sum += Math.floor(y / 10) + (y % 10);
    } else sum += x;
  });
  return [...d, (10 - (sum % 10)) % 10].join("");
}

/** Clinic-local (Asia/Riyadh) calendar date, `offsetDays` from today. */
export const riyadhDate = (offsetDays = 0) =>
  new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Riyadh" }).format(new Date(Date.now() + offsetDays * 86_400_000));

/** One-time link token from the development mailbox (emails are written there as HTML in Development). */
export async function mailboxToken(email: string, linkPath: string): Promise<string> {
  return (await mailboxLink(email, linkPath)).token;
}

/** The one-time link in the latest matching email: its token and whether it sits in the fragment (`#`) or the query (`?`). */
export async function mailboxLink(email: string, linkPath: string): Promise<{ token: string; separator: "#" | "?" }> {
  for (let attempt = 0; attempt < 20; attempt++) {
    if (fs.existsSync(env.mailbox)) {
      const files = fs
        .readdirSync(env.mailbox)
        .map((f) => path.join(env.mailbox, f))
        .sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs);
      for (const file of files) {
        const html = fs.readFileSync(file, "utf8");
        if (!html.toLowerCase().includes(email.toLowerCase())) continue;
        // Links carry the token in the fragment (`#token=`); the query form (`?token=`) is what older emails used.
        const match = html.match(new RegExp(`${linkPath}([?#])token=([^"&<\\s]+)`));
        if (match) return { token: decodeURIComponent(match[2]), separator: match[1] as "#" | "?" };
      }
    }
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error(`No ${linkPath} email for ${email} in ${env.mailbox}`);
}

export interface SessionData {
  accessToken: string;
  tenant?: { id: string } | null;
}

export const login = (email: string, password = PASSWORD) =>
  call<SessionData>("POST", "/auth/login", null, { email, password }).then((s) => s.accessToken);

// ------------------------------------------------------------------ platform admin (MFA)
// The platform account needs a second factor: this signs in through the real endpoints and enrols on first use. The
// authenticator secret of the LOCAL throw-away database is kept in test-results/ (git-ignored); E2E_PLATFORM_TOTP_SECRET
// can supply it instead. Nothing here is ever used outside a local test run.

const MFA_STATE_FILE = path.resolve(__dirname, "../../test-results/.platform-mfa.json");
const STEP_MS = 30_000;
const BASE32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

function base32Decode(text: string): Buffer {
  let bits = 0;
  let value = 0;
  const out: number[] = [];
  for (const c of text.replace(/[\s=]/g, "").toUpperCase()) {
    value = (value << 5) | BASE32.indexOf(c);
    bits += 5;
    if (bits >= 8) {
      out.push((value >>> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return Buffer.from(out);
}

/** RFC 6238 (HMAC-SHA1, 6 digits, 30 s) for an absolute time step. */
export function totpForStep(secretBase32: string, step: number): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const hmac = crypto.createHmac("sha1", base32Decode(secretBase32)).update(counter).digest();
  const offset = hmac[hmac.length - 1] & 0x0f;
  const binary = ((hmac[offset] & 0x7f) << 24) | (hmac[offset + 1] << 16) | (hmac[offset + 2] << 8) | hmac[offset + 3];
  return String(binary % 1_000_000).padStart(6, "0");
}

const lastUsedSteps = new Map<string, number>();

/**
 * A code the server will accept now. The server never accepts a time step twice per authenticator (replay
 * protection), so this keeps track of the steps it handed out and, when they are used up, waits for the clock
 * instead of reusing one.
 */
export async function freshTotp(secretBase32: string): Promise<string> {
  for (;;) {
    const now = Math.floor(Date.now() / STEP_MS);
    const step = Math.max((lastUsedSteps.get(secretBase32) ?? 0) + 1, now - 1);
    if (step <= now + 1) {
      lastUsedSteps.set(secretBase32, step);
      return totpForStep(secretBase32, step);
    }
    await new Promise((r) => setTimeout(r, (step - 1) * STEP_MS - Date.now() + 250));
  }
}

export function rememberPlatformSecret(secret: string) {
  fs.mkdirSync(path.dirname(MFA_STATE_FILE), { recursive: true });
  fs.writeFileSync(MFA_STATE_FILE, JSON.stringify({ secret }));
}

export function platformSecret(): string | null {
  if (process.env.E2E_PLATFORM_TOTP_SECRET) return process.env.E2E_PLATFORM_TOTP_SECRET;
  try {
    return (JSON.parse(fs.readFileSync(MFA_STATE_FILE, "utf8")) as { secret?: string }).secret ?? null;
  } catch {
    return null;
  }
}

let cachedPlatformToken: { token: string; at: number } | null = null;

/** Call after a spec changed the platform account's MFA or sessions. */
export const forgetPlatformSession = () => {
  cachedPlatformToken = null;
};

async function rawCall(method: string, url: string, token: string | null, body?: unknown) {
  const res = await fetch(`${env.apiUrl}/api/v1${url}`, {
    method,
    headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  let json: { data?: any; code?: string; details?: any } = {}; // eslint-disable-line @typescript-eslint/no-explicit-any
  try {
    json = await res.json();
  } catch {
    // empty body
  }
  return { status: res.status, json };
}

/** An access token for the platform admin (cached for ten minutes: each sign-in uses one authenticator time step). */
export async function platformLogin(): Promise<string> {
  if (cachedPlatformToken && Date.now() - cachedPlatformToken.at < 10 * 60_000) return cachedPlatformToken.token;
  if (!env.platformEmail || !env.platformPassword) throw new Error("Set E2E_PLATFORM_EMAIL / E2E_PLATFORM_PASSWORD — see e2e/README.md");

  for (let attempt = 0; attempt < 4; attempt++) {
    const first = await rawCall("POST", "/auth/login", null, { email: env.platformEmail, password: env.platformPassword });
    if (first.status === 200) return (cachedPlatformToken = { token: first.json.data.accessToken, at: Date.now() }).token;

    if (first.json.code === "MFA_ENROLLMENT_REQUIRED") {
      const start = await rawCall("POST", "/auth/mfa/enroll/start", null, { enrollmentToken: first.json.details.enrollmentToken });
      const secret: string = start.json.data.secret;
      const done = await rawCall("POST", "/auth/mfa/enroll/complete", null, {
        pendingToken: start.json.data.pendingToken,
        code: await freshTotp(secret),
      });
      if (done.status !== 200) throw new Error(`Platform MFA enrolment failed: ${done.status} ${done.json.code}`);
      rememberPlatformSecret(secret);
      return (cachedPlatformToken = { token: done.json.data.session.accessToken, at: Date.now() }).token;
    }

    if (first.json.code === "MFA_REQUIRED") {
      const secret = platformSecret();
      if (!secret) {
        throw new Error(
          "The platform admin already has an authenticator and its secret is unknown to the suite. Use a fresh local database, " +
            "or set E2E_PLATFORM_TOTP_SECRET (see e2e/README.md).",
        );
      }
      const verified = await rawCall("POST", "/auth/mfa/verify", null, {
        challengeToken: first.json.details.challengeToken,
        totpCode: await freshTotp(secret),
      });
      if (verified.status === 200) return (cachedPlatformToken = { token: verified.json.data.accessToken, at: Date.now() }).token;
      lastUsedSteps.set(secret, (lastUsedSteps.get(secret) ?? 0) + 1); // the server had already seen that step (an earlier run); move on
      continue;
    }
    throw new Error(`Platform sign-in failed: ${first.status} ${first.json.code}`);
  }
  throw new Error("Platform sign-in did not succeed after several authenticator steps.");
}
