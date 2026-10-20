"use client";

import { History } from "lucide-react";
import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { PageHeader, RequirePermission } from "@/components/app/page";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { DataTable, type Column } from "@/components/ui/data-table";
import { EmptyState } from "@/components/ui/empty-state";
import { Input, Select } from "@/components/ui/field";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { AuditLogItem } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { addDays, formatDateTime, isIsoDate, startOfDayUtc } from "@/lib/datetime";
import { useQuery } from "@/lib/hooks/use-query";
import { AUDIT_CATEGORIES, actionMessageKey, describeDetails } from "./audit-model";

const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";

/** The clinic's own audit trail, read-only (who did what, when, from where, with what result). */
export function AuditLogPage() {
  return (
    <RequirePermission anyOf={[P.AuditLogs.View]}>
      <AuditLog />
    </RequirePermission>
  );
}

function AuditLog() {
  const t = useTranslations("audit");
  const locale = useLocale();
  const { api, can } = useAuth();
  const { timeZone } = useClinic();
  const [page, setPage] = useState(1);
  const [category, setCategory] = useState("");
  const [userId, setUserId] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");

  const rangeInvalid = isIsoDate(from) && isIsoDate(to) && from > to;
  const fromUtc = isIsoDate(from) ? startOfDayUtc(from, timeZone) : undefined;
  const toUtc = isIsoDate(to) ? startOfDayUtc(addDays(to, 1), timeZone) : undefined;
  const filtered = Boolean(category || userId || from || to);

  const list = useQuery(rangeInvalid ? null : `audit:${page}:${category}:${userId}:${fromUtc}:${toUtc}`, (signal) =>
    api.auditLogs.list({ page, pageSize: 25, action: category || undefined, userId: userId || undefined, fromUtc, toUtc }, signal),
  );
  const staff = useQuery(can(P.Staff.View) ? "audit:staff" : null, async (signal) => (await api.staff.list({ pageSize: 100 }, signal)).items);
  const people = (staff.data ?? []).filter((s) => s.userId && s.userId !== EMPTY_GUID);

  const actionLabel = (action: string) => {
    const key = `actions.${actionMessageKey(action)}`;
    return t.has(key as never) ? t(key as never) : action;
  };
  const entityLabel = (type: string | null | undefined) => {
    if (!type) return "—";
    const key = `entities.${type}`;
    return t.has(key as never) ? t(key as never) : type;
  };
  const resetPage = <V,>(set: (v: V) => void) => (v: V) => {
    set(v);
    setPage(1);
  };

  const columns: Column<AuditLogItem>[] = [
    {
      id: "time",
      header: t("columns.time"),
      cell: (l) => <span className="ltr-nums whitespace-nowrap">{formatDateTime(l.occurredAtUtc, timeZone, locale)}</span>,
    },
    { id: "user", header: t("columns.user"), cell: (l) => l.userName ?? <span className="text-fg-subtle">{t("system")}</span> },
    { id: "action", header: t("columns.action"), cell: (l) => actionLabel(l.action) },
    { id: "entity", header: t("columns.entity"), hideable: true, cell: (l) => entityLabel(l.entityType) },
    {
      id: "result",
      header: t("columns.result"),
      cell: (l) =>
        l.result === "Success" ? (
          <span className="text-fg-muted">{t("results.Success")}</span>
        ) : (
          <Badge tone={l.result === "Denied" ? "danger" : "warning"}>{t(`results.${l.result}`)}</Badge>
        ),
    },
    {
      id: "ip",
      header: t("columns.ip"),
      hideable: true,
      defaultHidden: true,
      cell: (l) => (l.ipAddress ? <span dir="ltr" className="ltr-nums text-fg-muted">{l.ipAddress}</span> : "—"),
    },
    {
      id: "details",
      header: t("columns.details"),
      hideable: true,
      defaultHidden: true,
      cell: (l) => {
        const text = describeDetails(l.details);
        return text ? (
          <span dir="ltr" className="break-all font-mono text-xs text-fg-muted">
            {text}
          </span>
        ) : (
          "—"
        );
      },
    },
  ];

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} />
      <Card>
        <DataTable
          caption={t("title")}
          columns={columns}
          rows={rangeInvalid ? [] : list.data?.items}
          rowKey={(l) => String(l.id)}
          loading={list.loading}
          error={list.error ? (isApiError(list.error) ? list.error.message : t("loadFailed")) : null}
          onRetry={list.reload}
          meta={list.data?.meta}
          onPageChange={setPage}
          toolbar={
            <div className="flex flex-wrap items-end gap-3">
              <Select
                label={t("filters.category")}
                value={category}
                onChange={(e) => resetPage(setCategory)(e.target.value)}
                placeholder={t("filters.allCategories")}
                options={AUDIT_CATEGORIES.map((c) => ({ value: c, label: t(`categories.${c.slice(0, -1)}` as never) }))}
                className="w-44"
              />
              {can(P.Staff.View) ? (
                <Select
                  label={t("filters.user")}
                  value={userId}
                  onChange={(e) => resetPage(setUserId)(e.target.value)}
                  placeholder={t("filters.allUsers")}
                  options={people.map((s) => ({ value: s.userId, label: s.fullName }))}
                  className="w-48"
                />
              ) : null}
              <Input label={t("filters.from")} type="date" value={from} max={to || undefined} onChange={(e) => resetPage(setFrom)(e.target.value)} className="w-40" />
              <Input
                label={t("filters.to")}
                type="date"
                value={to}
                min={from || undefined}
                onChange={(e) => resetPage(setTo)(e.target.value)}
                error={rangeInvalid ? t("filters.rangeInvalid") : undefined}
                className="w-40"
              />
              {filtered ? (
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => {
                    setCategory("");
                    setUserId("");
                    setFrom("");
                    setTo("");
                    setPage(1);
                  }}
                >
                  {t("filters.clear")}
                </Button>
              ) : null}
            </div>
          }
          empty={
            <EmptyState
              icon={<History className="size-6" />}
              title={filtered ? t("noResults") : t("emptyTitle")}
              description={filtered ? t("noResultsHint") : undefined}
            />
          }
        />
      </Card>
    </>
  );
}
