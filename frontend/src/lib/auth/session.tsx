"use client";

import { useLocale, useTranslations } from "next-intl";
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { ApiClient, apiBaseUrl, ClientErrorCodes, type ApiError } from "@/lib/api/client";
import { createEndpoints, type Endpoints } from "@/lib/api/endpoints";
import type { Session } from "@/lib/api/types";

type AuthStatus = "loading" | "authenticated" | "anonymous";

interface AuthContextValue {
  status: AuthStatus;
  session: Session | null;
  api: Endpoints;
  /** UI gating only — the API enforces every permission itself. */
  can: (permission: string) => boolean;
  canAny: (...permissions: string[]) => boolean;
  logout: () => Promise<void>;
  /** True after the user signed out explicitly (no "return to" URL on the login page). */
  signedOut: boolean;
  /** Last plan-related 403 (limit reached / feature not in plan), shown as an "upgrade plan" prompt. */
  upgradeError: ApiError | null;
  clearUpgradeError: () => void;
  /** Some call was refused with TENANT_SUSPENDED: the platform suspended this clinic (takes effect immediately). */
  tenantSuspended: boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

/**
 * Owns the single ApiClient instance. On first mount it tries to restore the session from the httpOnly refresh
 * cookie (the access token is never persisted, so every page load starts here).
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const locale = useLocale();
  const tErrors = useTranslations("errors");
  const [status, setStatus] = useState<AuthStatus>("loading");
  const [session, setSession] = useState<Session | null>(null);
  const [upgradeError, setUpgradeError] = useState<ApiError | null>(null);
  const [signedOut, setSignedOut] = useState(false);
  const [tenantSuspended, setTenantSuspended] = useState(false);
  const [client] = useState(
    () =>
      new ApiClient({
        baseUrl: apiBaseUrl,
        onSessionChange: (next) => {
          setSession(next);
          setTenantSuspended(false);
          setStatus(next ? "authenticated" : "anonymous");
          if (next) setSignedOut(false);
        },
        onError: (error) => {
          if (error.isUpgradeRequired) setUpgradeError(error);
          if (error.code === "TENANT_SUSPENDED") setTenantSuspended(true);
        },
      }),
  );
  const api = useMemo(() => createEndpoints(client), [client]);

  useEffect(() => {
    client.configure({
      getLanguage: () => locale,
      fallbackMessage: (code) => tErrors(code === ClientErrorCodes.Network ? "network" : "unexpected"),
    });
  }, [client, locale, tErrors]);

  useEffect(() => {
    if (!client.hasAccessToken) void client.refresh();
  }, [client]);

  const permissions = useMemo(() => new Set(session?.permissions ?? []), [session]);
  const can = useCallback((p: string) => permissions.has(p), [permissions]);
  const canAny = useCallback((...ps: string[]) => ps.some((p) => permissions.has(p)), [permissions]);
  const logout = useCallback(async () => {
    setSignedOut(true);
    await api.auth.logout();
  }, [api]);
  const clearUpgradeError = useCallback(() => setUpgradeError(null), []);

  const value = useMemo<AuthContextValue>(
    () => ({ status, session, api, can, canAny, logout, signedOut, upgradeError, clearUpgradeError, tenantSuspended }),
    [status, session, api, can, canAny, logout, signedOut, upgradeError, clearUpgradeError, tenantSuspended],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}

/** Shorthand for components that only need the endpoints. */
export function useApi() {
  return useAuth().api;
}
