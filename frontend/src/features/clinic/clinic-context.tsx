"use client";

import { createContext, useContext, useMemo, type ReactNode } from "react";
import type { Branch, ClinicAccess, ClinicSettings } from "@/lib/api/types";
import { useAuth } from "@/lib/auth/session";
import { DEFAULT_TIME_ZONE } from "@/lib/datetime";
import { useQuery } from "@/lib/hooks/use-query";

interface ClinicContextValue {
  settings: ClinicSettings | undefined;
  timeZone: string;
  currency: string;
  branches: Branch[];
  /** Branches this member may work in (all when unrestricted). */
  allowedBranches: Branch[];
  /** The member's branch ids from /auth/me; empty = unrestricted (all branches). */
  branchScope: string[];
  branchName: (id: string | null | undefined) => string;
  /**
   * The signed-in user's own doctor profile in this clinic, from GET /auth/me (resolved by the server from the
   * session — never guessed or stored by the client). null = not a doctor here; undefined = still loading.
   */
  ownDoctorId: string | null | undefined;
  /** Same rule the API enforces on every request (subscription period / suspension); "Full" until /auth/me loads. */
  access: ClinicAccess;
  reloadSettings: () => void;
  reloadBranches: () => void;
}

const ClinicContext = createContext<ClinicContextValue | null>(null);

/** Per-clinic reference data loaded once per tenant: settings (time zone, currency), branches, branch scope. */
export function ClinicProvider({ tenantId, children }: { tenantId: string; children: ReactNode }) {
  const { api } = useAuth();
  const settings = useQuery(`settings:${tenantId}`, () => api.settings.get());
  const branches = useQuery(`branches:${tenantId}`, () => api.branches.list());
  const me = useQuery(`me:${tenantId}`, () => api.auth.me());

  const value = useMemo<ClinicContextValue>(() => {
    const list = branches.data ?? [];
    const scope = me.data?.branchIds ?? [];
    const allowed = scope.length === 0 ? list : list.filter((b) => scope.includes(b.id));
    return {
      settings: settings.data,
      timeZone: settings.data?.timeZone || DEFAULT_TIME_ZONE,
      currency: settings.data?.currency || "SAR",
      branches: list,
      allowedBranches: allowed,
      branchScope: scope,
      branchName: (id) => list.find((b) => b.id === id)?.name ?? "",
      ownDoctorId: me.data ? (me.data.doctorId ?? null) : me.error ? null : undefined,
      access: me.data?.access ?? "Full",
      reloadSettings: settings.reload,
      reloadBranches: branches.reload,
    };
  }, [settings.data, settings.reload, branches.data, branches.reload, me.data, me.error]);

  return <ClinicContext.Provider value={value}>{children}</ClinicContext.Provider>;
}

export function useClinic() {
  const ctx = useContext(ClinicContext);
  if (!ctx) throw new Error("useClinic must be used inside <ClinicProvider>");
  return ctx;
}
