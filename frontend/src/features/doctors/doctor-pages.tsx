"use client";

import { Stethoscope, UserPlus } from "lucide-react";
import Link from "next/link";
import { useParams, useRouter, useSearchParams } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { PageHeader, RequirePermission } from "@/components/app/page";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardBody, CardFooter } from "@/components/ui/card";
import { DataTable, type Column } from "@/components/ui/data-table";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Select } from "@/components/ui/field";
import { LoadingBlock } from "@/components/ui/spinner";
import { Tabs } from "@/components/ui/tabs";
import { useToast } from "@/components/ui/toast";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { Doctor } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { formatMoney, trimSeconds } from "@/lib/datetime";
import { useForm } from "@/lib/hooks/use-form";
import { useQuery } from "@/lib/hooks/use-query";
import { seatLabel } from "@/features/staff/seat";
import { ConflictAlert, DoctorFields, doctorFormValues, toDoctorRequest, validateDoctor, type DoctorFormValues } from "./doctor-form";
import { DAYS, ScheduleEditor, weekdayNames } from "./schedule-editor";

// ------------------------------------------------------------------ list

export function DoctorsList() {
  return (
    <RequirePermission anyOf={[P.Doctors.View]}>
      <List />
    </RequirePermission>
  );
}

function List() {
  const t = useTranslations("doctors");
  const locale = useLocale();
  const { api, can } = useAuth();
  const { currency } = useClinic();
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState("");
  const [adding, setAdding] = useState(false);
  const list = useQuery(`doctors:${page}:${search}`, (signal) => api.doctors.list({ page, pageSize: 20, search: search || undefined }, signal));
  const names = weekdayNames(locale);

  const columns: Column<Doctor>[] = [
    {
      id: "name",
      header: t("fields.displayNameAr"),
      cell: (d) => (
        <Link href={`/doctors/${d.id}`} className="font-medium text-primary-strong hover:underline">
          {d.displayNameAr}
        </Link>
      ),
    },
    { id: "specialty", header: t("fields.specialty"), cell: (d) => d.specialty },
    {
      id: "days",
      header: t("workingDays"),
      hideable: true,
      cell: (d) => {
        const days = DAYS.filter((day) => d.schedule.some((w) => w.dayOfWeek === day));
        return days.length ? days.map((day) => names[day]).join("، ") : <span className="text-fg-subtle">{t("noSchedule")}</span>;
      },
    },
    {
      id: "fee",
      header: t("fields.consultationFee"),
      hideable: true,
      cell: (d) => <span className="ltr-nums">{formatMoney(d.consultationFee, currency, locale)}</span>,
    },
    {
      id: "status",
      header: t("status"),
      cell: (d) => (d.isActive ? <Badge tone="success">{t("active")}</Badge> : <Badge tone="neutral">{t("inactive")}</Badge>),
    },
  ];

  return (
    <>
      <PageHeader
        title={t("title")}
        description={t("subtitle")}
        actions={
          can(P.Doctors.Manage) && can(P.Staff.View) ? (
            <Button icon={<UserPlus className="size-4" aria-hidden="true" />} onClick={() => setAdding(true)}>
              {t("add")}
            </Button>
          ) : null
        }
      />
      <Card>
        <DataTable
          caption={t("title")}
          columns={columns}
          rows={list.data?.items}
          rowKey={(d) => d.id}
          loading={list.loading}
          error={list.error ? (isApiError(list.error) ? list.error.message : t("loadFailed")) : null}
          onRetry={list.reload}
          meta={list.data?.meta}
          onPageChange={setPage}
          search={search}
          onSearchChange={(v) => {
            setSearch(v);
            setPage(1);
          }}
          searchPlaceholder={t("searchPlaceholder")}
          empty={<EmptyState icon={<Stethoscope className="size-6" />} title={t("emptyTitle")} description={t("emptyDescription")} />}
        />
      </Card>
      {adding ? <AddDoctorDialog onClose={() => setAdding(false)} /> : null}
    </>
  );
}

/** A doctor profile is attached to an existing staff seat (Platform → Tenant → Users → Doctor profile). */
function AddDoctorDialog({ onClose }: { onClose: () => void }) {
  const t = useTranslations("doctors");
  const tc = useTranslations("common");
  const router = useRouter();
  const toast = useToast();
  const { api } = useAuth();
  const tStaff = useTranslations("staff");
  const { settings } = useClinic();
  const staff = useQuery("staff:candidates", async (signal) => (await api.staff.list({ pageSize: 100 }, signal)).items);
  const candidates = (staff.data ?? []).filter((s) => !s.doctorId && s.status !== "Disabled");
  const [membershipId, setMembershipId] = useState("");
  const form = useForm<DoctorFormValues>(doctorFormValues(undefined, settings?.defaultAppointmentMinutes ?? 15));

  const save = () =>
    void form.submit(
      (v) => ({ ...validateDoctor(v), membershipId: membershipId ? null : "required" }),
      async (v) => {
        const created = await api.doctors.create({ ...toDoctorRequest(v), membershipId });
        toast({ tone: "success", title: t("created") });
        router.push(`/doctors/${created.id}?tab=schedule`);
      },
    );

  return (
    <Dialog
      open
      onClose={onClose}
      size="lg"
      title={t("add")}
      description={t("addDescription")}
      dismissible={!form.submitting}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={form.submitting}>
            {tc("cancel")}
          </Button>
          <Button onClick={save} loading={form.submitting}>
            {tc("create")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {form.formError && !form.formError.isUpgradeRequired ? <Alert tone="danger">{form.formError.message}</Alert> : null}
        <Select
          label={t("fields.staffMember")}
          required
          value={membershipId}
          onChange={(e) => {
            setMembershipId(e.target.value);
            const person = candidates.find((c) => c.membershipId === e.target.value);
            // A pending seat's name is masked (it equals the email), so it is not offered as the doctor's name.
            if (person && person.status !== "Invited" && !form.values.displayNameAr) form.setField("displayNameAr", person.fullName);
          }}
          placeholder={staff.loading ? tc("loading") : t("chooseStaff")}
          hint={t("staffHint")}
          error={form.errors.membershipId}
          options={candidates.map((c) => ({ value: c.membershipId, label: seatLabel(c, tStaff("statuses.Invited")) }))}
        />
        <DoctorFields form={form} />
      </div>
    </Dialog>
  );
}

// ------------------------------------------------------------------ details (profile + working hours)

export function DoctorPage() {
  return (
    <RequirePermission anyOf={[P.Doctors.View]}>
      <DoctorDetails />
    </RequirePermission>
  );
}

function DoctorDetails() {
  const t = useTranslations("doctors");
  const locale = useLocale();
  const { id } = useParams<{ id: string }>();
  const { api, can } = useAuth();
  const { branchName } = useClinic();
  const doctor = useQuery(`doctor:${id}`, (signal) => api.doctors.get(id, signal));
  const params = useSearchParams();
  const [tab, setTab] = useState(params.get("tab") === "schedule" ? "schedule" : "profile");
  const canManage = can(P.Doctors.Manage);
  const names = weekdayNames(locale);

  if (doctor.initialLoading) return <LoadingBlock label={t("loading")} />;
  if (!doctor.data) {
    // 404 also covers doctors outside a branch-restricted member's branches (T-011): same answer as "doesn't exist".
    if (isApiError(doctor.error) && doctor.error.status === 404) {
      return (
        <EmptyState
          icon={<Stethoscope className="size-6" />}
          title={t("notFoundTitle")}
          description={t("notFoundDescription")}
          action={
            <Link href="/doctors" className="font-medium text-primary-strong hover:underline">
              {t("backToList")}
            </Link>
          }
          className="py-24"
        />
      );
    }
    return doctor.error ? <Alert tone="danger">{isApiError(doctor.error) ? doctor.error.message : t("loadFailed")}</Alert> : null;
  }
  const d = doctor.data;

  return (
    <>
      <PageHeader
        title={d.displayNameAr}
        description={d.specialty}
        breadcrumbs={[{ label: t("title"), href: "/doctors" }, { label: d.displayNameAr }]}
      />
      {canManage ? (
        <Tabs
          label={t("title")}
          value={tab}
          onChange={setTab}
          tabs={[
            {
              id: "profile",
              label: t("tabs.profile"),
              content: <ProfileForm key={d.rowVersion} doctor={d} onSaved={(saved) => doctor.setData(() => saved)} onReload={doctor.reload} />,
            },
            {
              id: "schedule",
              label: t("tabs.schedule"),
              content: (
                <Card>
                  <CardBody>
                    <ScheduleEditor key={d.rowVersion} doctor={d} onSaved={(saved) => doctor.setData(() => saved)} onReload={doctor.reload} />
                  </CardBody>
                </Card>
              ),
            },
          ]}
        />
      ) : (
        <Card>
          <CardBody>
            <h2 className="mb-3 font-semibold">{t("tabs.schedule")}</h2>
            {d.schedule.length === 0 ? (
              <p className="text-sm text-fg-muted">{t("noSchedule")}</p>
            ) : (
              <ul className="flex flex-col gap-1 text-sm">
                {d.schedule.map((w, i) => (
                  <li key={i}>
                    <span className="font-medium">{names[w.dayOfWeek]}</span>:{" "}
                    <span className="ltr-nums">
                      {trimSeconds(w.startTime)}–{trimSeconds(w.endTime)}
                    </span>{" "}
                    <span className="text-fg-subtle">({branchName(w.branchId)})</span>
                  </li>
                ))}
              </ul>
            )}
          </CardBody>
        </Card>
      )}
    </>
  );
}

function ProfileForm({ doctor, onSaved, onReload }: { doctor: Doctor; onSaved: (d: Doctor) => void; onReload: () => void }) {
  const tc = useTranslations("common");
  const toast = useToast();
  const { api } = useAuth();
  const form = useForm<DoctorFormValues>(doctorFormValues(doctor));

  return (
    <form
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void form.submit(validateDoctor, async (v) => {
          const saved = await api.doctors.update(doctor.id, { ...toDoctorRequest(v), isActive: v.isActive, rowVersion: doctor.rowVersion });
          toast({ tone: "success", title: tc("saved") });
          onSaved(saved);
        });
      }}
    >
      <Card>
        <CardBody className="flex flex-col gap-4">
          {form.formError?.isConcurrencyConflict ? (
            <ConflictAlert onReload={onReload} />
          ) : form.formError && !form.formError.isUpgradeRequired ? (
            <Alert tone="danger">{form.formError.message}</Alert>
          ) : null}
          <DoctorFields form={form} showActive />
        </CardBody>
        <CardFooter>
          <Button type="submit" loading={form.submitting}>
            {tc("saveChanges")}
          </Button>
        </CardFooter>
      </Card>
    </form>
  );
}
