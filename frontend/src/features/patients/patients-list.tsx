"use client";

import { UserPlus, Users } from "lucide-react";
import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { PageHeader, RequirePermission } from "@/components/app/page";
import { Badge } from "@/components/ui/badge";
import { ButtonLink } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { DataTable, type Column, type SortState, formatSort } from "@/components/ui/data-table";
import { EmptyState } from "@/components/ui/empty-state";
import { Select } from "@/components/ui/field";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { Gender, PatientListItem } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { ageInYears, formatShortDate, todayInZone } from "@/lib/datetime";
import { useQuery } from "@/lib/hooks/use-query";
import { toLocalMobile } from "@/lib/validation/saudi";

export function PatientsList() {
  return (
    <RequirePermission anyOf={[P.Patients.View]}>
      <List />
    </RequirePermission>
  );
}

function List() {
  const t = useTranslations("patients");
  const locale = useLocale();
  const { api, can } = useAuth();
  const { timeZone } = useClinic();
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState("");
  const [sort, setSort] = useState<SortState | undefined>({ key: "createdAt", desc: true });
  const [gender, setGender] = useState<Gender | "">("");
  const today = todayInZone(timeZone);

  const sortParam = formatSort(sort);
  const list = useQuery(`patients:${page}:${search}:${sortParam}:${gender}`, (signal) =>
    api.patients.list({ page, pageSize: 20, search: search || undefined, sort: sortParam, gender: gender || undefined }, signal),
  );

  const columns: Column<PatientListItem>[] = [
    {
      id: "mrn",
      header: t("fields.mrn"),
      sortKey: "mrn",
      cell: (p) => <span className="ltr-nums text-fg-muted">{p.medicalRecordNumber}</span>,
    },
    {
      id: "name",
      header: t("fields.fullName"),
      sortKey: "fullName",
      cell: (p) => (
        <Link href={`/patients/${p.id}`} className="font-medium text-primary-strong hover:underline">
          {p.fullName}
        </Link>
      ),
    },
    { id: "mobile", header: t("fields.mobile"), cell: (p) => <span className="ltr-nums">{toLocalMobile(p.mobileNumber)}</span> },
    {
      id: "gender",
      header: t("fields.gender"),
      hideable: true,
      cell: (p) => <Badge tone="neutral">{t(`gender.${p.gender}`)}</Badge>,
    },
    {
      id: "age",
      header: t("fields.age"),
      hideable: true,
      cell: (p) => (p.dateOfBirth ? t("ageYears", { count: ageInYears(p.dateOfBirth, today) }) : "—"),
    },
    {
      id: "created",
      header: t("fields.registeredAt"),
      sortKey: "createdAt",
      hideable: true,
      cell: (p) => <span className="ltr-nums">{formatShortDate(p.createdAtUtc.slice(0, 10), locale)}</span>,
    },
  ];

  return (
    <>
      <PageHeader
        title={t("title")}
        description={t("subtitle")}
        actions={
          can(P.Patients.Create) ? (
            <ButtonLink href="/patients/new" icon={<UserPlus className="size-4" aria-hidden="true" />}>
              {t("new")}
            </ButtonLink>
          ) : null
        }
      />
      <Card>
        <DataTable
          caption={t("title")}
          columns={columns}
          rows={list.data?.items}
          rowKey={(p) => p.id}
          loading={list.loading}
          error={list.error ? (isApiError(list.error) ? list.error.message : t("loadFailed")) : null}
          onRetry={list.reload}
          meta={list.data?.meta}
          onPageChange={setPage}
          sort={sort}
          onSortChange={(s) => {
            setSort(s);
            setPage(1);
          }}
          search={search}
          onSearchChange={(v) => {
            setSearch(v);
            setPage(1);
          }}
          searchPlaceholder={t("searchPlaceholder")}
          toolbar={
            <Select
              label={t("fields.gender")}
              hideLabel
              value={gender}
              onChange={(e) => {
                setGender(e.target.value as Gender | "");
                setPage(1);
              }}
              placeholder={t("allGenders")}
              options={(["Male", "Female"] as const).map((g) => ({ value: g, label: t(`gender.${g}`) }))}
              className="w-36"
            />
          }
          empty={
            <EmptyState
              icon={<Users className="size-6" />}
              title={search ? t("noResults") : t("emptyTitle")}
              description={search ? t("noResultsHint") : t("emptyDescription")}
            />
          }
        />
      </Card>
    </>
  );
}
