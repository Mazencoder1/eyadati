import type { PageMeta, Paged, Session } from "./types";

export type FieldErrors = Record<string, string[]>;

/** Codes produced by the client itself (the server never sends these). */
export const ClientErrorCodes = {
  Network: "NETWORK_ERROR",
  Unexpected: "UNEXPECTED_RESPONSE",
} as const;

/** Unified error for every failed call. `message` is the server's localized message whenever there is one. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly errors?: FieldErrors;
  readonly details?: unknown;
  readonly correlationId?: string;
  /** From the Retry-After header of a 429. */
  readonly retryAfterSeconds?: number;

  constructor(init: {
    status: number;
    code: string;
    message: string;
    errors?: FieldErrors;
    details?: unknown;
    correlationId?: string;
    retryAfterSeconds?: number;
  }) {
    super(init.message);
    this.name = "ApiError";
    this.status = init.status;
    this.code = init.code;
    this.errors = init.errors;
    this.details = init.details;
    this.correlationId = init.correlationId;
    this.retryAfterSeconds = init.retryAfterSeconds;
  }

  /** 403 caused by the plan (limit reached or feature not included) — show "upgrade plan". */
  get isUpgradeRequired(): boolean {
    return this.status === 403 && (this.code === "USAGE_LIMIT_REACHED" || this.code === "FEATURE_NOT_AVAILABLE");
  }

  get isConcurrencyConflict(): boolean {
    return this.code === "CONCURRENCY_CONFLICT";
  }

  get isValidation(): boolean {
    return this.status === 400 && !!this.errors;
  }
}

export function isApiError(e: unknown): e is ApiError {
  return e instanceof ApiError;
}

type Query = Record<string, string | number | boolean | null | undefined>;

export interface RequestOptions {
  query?: Query;
  body?: unknown;
  signal?: AbortSignal;
  /** Public endpoints (login, register…): no bearer token and no refresh-on-401. */
  anonymous?: boolean;
}

export interface ApiClientOptions {
  baseUrl: string;
  fetchImpl?: typeof fetch;
  /** UI language sent as Accept-Language so server messages come back in that language. */
  getLanguage?: () => string;
  /** Messages for failures that never reached the server (network down, non-JSON proxy errors). */
  fallbackMessage?: (code: string) => string;
  /** Called whenever the session changes: after login/refresh/switch (a session) or when it ends (null). */
  onSessionChange?: (session: Session | null) => void;
  /** Observes every failed call (used for app-wide reactions such as the "upgrade plan" prompt). */
  onError?: (error: ApiError) => void;
}

interface Envelope {
  success?: boolean;
  data?: unknown;
  meta?: PageMeta;
  code?: string;
  message?: string;
  errors?: FieldErrors;
  details?: unknown;
  correlationId?: string;
}

const API_PREFIX = "/api/v1";

const delay = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

const REFRESH_LOCK = "clinic-saas.session-refresh";

/**
 * Tabs of one browser share the refresh cookie. The server rotates it on every refresh and treats a replayed token as
 * theft, so tabs refreshing at the same moment (a restored browser session, links opened together) would end the
 * session everywhere (P3-SEC-25). The Web Locks API makes them take turns: each tab then sends the cookie that the
 * previous one just received. Where the API is missing (old browsers) refreshes simply run as before.
 */
async function acrossTabs<T>(task: () => Promise<T>): Promise<T> {
  const locks = typeof navigator === "undefined" ? undefined : navigator.locks;
  return locks ? await locks.request(REFRESH_LOCK, task) : await task();
}

/**
 * A 401 that answers a password or MFA-code check (a wrong current password on change-password, a wrong code when
 * regenerating recovery codes or removing MFA) rather than an expired token. Refreshing and re-sending it would count
 * a second failed attempt against rate limiting and lockout.
 */
async function isCredentialRejection(response: Response): Promise<boolean> {
  try {
    const body = (await response.clone().json()) as Envelope;
    return body?.code === "INVALID_CREDENTIALS" || body?.code === "INVALID_MFA_CODE";
  } catch {
    return false;
  }
}

/**
 * HTTP client for the ClinicSaaS API.
 *
 * Security model:
 * - The access token lives only in this object's memory (never localStorage/sessionStorage), so an XSS payload
 *   can't read a long-lived credential from storage, and a page reload simply re-runs the refresh.
 * - The refresh token is an httpOnly cookie scoped to /api/v1/auth on the API origin; JavaScript never sees it.
 *   `credentials: "include"` lets the browser send/receive it.
 * - On 401 the client refreshes once (single-flight: concurrent 401s share one refresh) and retries the original
 *   request exactly once. A failed refresh ends the session.
 */
export class ApiClient {
  private accessToken: string | null = null;
  private refreshInFlight: Promise<Session | null> | null = null;
  private readonly fetchImpl: typeof fetch;

  private options: ApiClientOptions;

  constructor(options: ApiClientOptions) {
    this.options = { ...options };
    // Bind so a bare global fetch keeps its `this` (required by browsers).
    this.fetchImpl = options.fetchImpl ?? ((input, init) => fetch(input, init));
  }

  /** Updates UI-dependent settings (language, fallback messages) without recreating the client. */
  configure(options: Partial<Pick<ApiClientOptions, "getLanguage" | "fallbackMessage">>): void {
    this.options = { ...this.options, ...options };
  }

  get hasAccessToken(): boolean {
    return this.accessToken !== null;
  }

  /** Stores the session's access token (memory only) and notifies listeners. */
  setSession(session: Session | null): void {
    this.accessToken = session?.accessToken ?? null;
    this.options.onSessionChange?.(session);
  }

  get<T>(path: string, options?: Omit<RequestOptions, "body">): Promise<T> {
    return this.request<T>("GET", path, options);
  }

  post<T = void>(path: string, body?: unknown, options?: Omit<RequestOptions, "body">): Promise<T> {
    return this.request<T>("POST", path, { ...options, body });
  }

  put<T>(path: string, body?: unknown, options?: Omit<RequestOptions, "body">): Promise<T> {
    return this.request<T>("PUT", path, { ...options, body });
  }

  delete(path: string, options?: Omit<RequestOptions, "body">): Promise<void> {
    return this.request<void>("DELETE", path, options);
  }

  /** Returns `data` of a success envelope (undefined for 202/204). */
  async request<T>(method: string, path: string, options: RequestOptions = {}): Promise<T> {
    const envelope = await this.send(method, path, options);
    return envelope?.data as T;
  }

  /** For list endpoints: `{ data: [...], meta: {...} }` → `{ items, meta }`. */
  async getPaged<T>(path: string, options: Omit<RequestOptions, "body"> = {}): Promise<Paged<T>> {
    const envelope = await this.send("GET", path, options);
    const items = (envelope?.data as T[]) ?? [];
    const meta = envelope?.meta ?? { page: 1, pageSize: items.length, totalCount: items.length, totalPages: 1 };
    return { items, meta };
  }

  /**
   * Exchanges the refresh cookie for a new session. Concurrent callers share one network call (the server
   * rotates refresh tokens and treats reuse as theft, so two parallel refreshes would end the session), and tabs of
   * the same browser take turns (see {@link acrossTabs}).
   */
  refresh(): Promise<Session | null> {
    if (!this.refreshInFlight) {
      this.refreshInFlight = acrossTabs(() => this.doRefresh()).finally(() => {
        this.refreshInFlight = null;
      });
    }
    return this.refreshInFlight;
  }

  private async doRefresh(): Promise<Session | null> {
    for (let attempt = 0; ; attempt++) {
      try {
        const envelope = await this.send("POST", "/auth/refresh", { body: {}, anonymous: true }, false);
        const session = envelope?.data as Session;
        this.setSession(session);
        return session;
      } catch (e) {
        // Rate limited (e.g. many reloads behind a shared clinic IP): the cookie is still valid, so wait and try
        // once more instead of signing the user out.
        if (attempt === 0 && e instanceof ApiError && e.status === 429) {
          await delay(Math.min(e.retryAfterSeconds ?? 2, 10) * 1000);
          continue;
        }
        this.setSession(null);
        return null;
      }
    }
  }

  private async send(method: string, path: string, options: RequestOptions, notify = true): Promise<Envelope | undefined> {
    const first = await this.fetchOnce(method, path, options);
    if (first.status === 401 && !options.anonymous && !(await isCredentialRejection(first))) {
      const session = await this.refresh();
      if (session) {
        const retried = await this.fetchOnce(method, path, options);
        return this.handle(retried, notify);
      }
    }
    return this.handle(first, notify);
  }

  private async fetchOnce(method: string, path: string, options: RequestOptions): Promise<Response> {
    const headers: Record<string, string> = { Accept: "application/json" };
    const language = this.options.getLanguage?.();
    if (language) headers["Accept-Language"] = language;
    if (options.body !== undefined) headers["Content-Type"] = "application/json";
    if (!options.anonymous && this.accessToken) headers.Authorization = `Bearer ${this.accessToken}`;

    try {
      return await this.fetchImpl(this.url(path, options.query), {
        method,
        headers,
        body: options.body === undefined ? undefined : JSON.stringify(options.body),
        credentials: "include",
        signal: options.signal,
        cache: "no-store",
      });
    } catch (e) {
      if (e instanceof DOMException && e.name === "AbortError") throw e;
      throw this.raise(
        new ApiError({
          status: 0,
          code: ClientErrorCodes.Network,
          message: this.options.fallbackMessage?.(ClientErrorCodes.Network) ?? "Network error",
        }),
      );
    }
  }

  private async handle(response: Response, notify: boolean): Promise<Envelope | undefined> {
    if (response.status === 204 || response.status === 202) return undefined;

    let envelope: Envelope | undefined;
    const type = response.headers.get("content-type") ?? "";
    if (type.includes("json")) {
      try {
        envelope = (await response.json()) as Envelope;
      } catch {
        envelope = undefined;
      }
    }

    if (response.ok) return envelope;

    const error = new ApiError({
      status: response.status,
      code: envelope?.code ?? ClientErrorCodes.Unexpected,
      message:
        envelope?.message ?? this.options.fallbackMessage?.(ClientErrorCodes.Unexpected) ?? `HTTP ${response.status}`,
      errors: envelope?.errors,
      details: envelope?.details,
      correlationId: envelope?.correlationId ?? response.headers.get("x-correlation-id") ?? undefined,
      retryAfterSeconds: Number(response.headers.get("retry-after")) || undefined,
    });
    throw notify ? this.raise(error) : error;
  }

  private raise(error: ApiError): ApiError {
    try {
      this.options.onError?.(error);
    } catch {
      // listeners must never break the caller's error handling
    }
    return error;
  }

  private url(path: string, query?: Query): string {
    const base = this.options.baseUrl.replace(/\/+$/, "");
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(query ?? {})) {
      if (value === undefined || value === null || value === "") continue;
      params.set(key, String(value));
    }
    const qs = params.toString();
    return `${base}${API_PREFIX}${path}${qs ? `?${qs}` : ""}`;
  }
}

export const apiBaseUrl = (process.env.NEXT_PUBLIC_API_URL || "http://localhost:5056").replace(/\/+$/, "");
