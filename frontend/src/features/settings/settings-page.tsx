"use client";

import { Building2, MapPin, Pencil, Plus, Trash2 } from "lucide-react";
import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { PageHeader, RequirePermission } from "@/components/app/page";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardBody, CardFooter, CardHeader } from "@/components/ui/card";
import { DataTable, type Column } from "@/components/ui/data-table";
import { ConfirmDialog, Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Checkbox, Input, Select } from "@/components/ui/field";
import { LoadingBlock } from "@/components/ui/spinner";
import { Tabs } from "@/components/ui/tabs";
import { useToast } from "@/components/ui/toast";
import { ConflictAlert } from "@/features/doctors/doctor-form";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { Branch, ClinicService, ClinicSettings } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { canCreateBranch, canEditBranch } from "@/lib/auth/branch-scope";
import { useAuth } from "@/lib/auth/session";
import { formatMoney } from "@/lib/datetime";
import { useForm } from "@/lib/hooks/use-form";
import { useQuery } from "@/lib/hooks/use-query";
import { emptyToNull } from "@/lib/utils";
import { check, rules, toLatinDigits, toLocalMobile } from "@/lib/validation/saudi";

const TIME_ZONES = ["Asia/Riyadh", "Asia/Dubai", "Asia/Kuwait", "Asia/Bahrain", "Asia/Qatar", "Asia/Muscat", "Africa/Cairo"];

export function SettingsPage() {
  const t = useTranslations("settings");
  const { can } = useAuth();
  const tabs = [
    ...(can(P.Settings.Manage) ? [{ id: "clinic", label: t("tabs.clinic"), content: <ClinicTab /> }] : []),
    ...(can(P.Branches.Manage) ? [{ id: "branches", label: t("tabs.branches"), content: <BranchesTab /> }] : []),
    ...(can(P.Settings.Manage) ? [{ id: "services", label: t("tabs.services"), content: <ServicesTab /> }] : []),
  ];
  const [tab, setTab] = useState(tabs[0]?.id ?? "clinic");
  return (
    <RequirePermission anyOf={[P.Settings.Manage, P.Branches.Manage]}>
      <PageHeader title={t("title")} description={t("subtitle")} />
      {tabs.length > 0 ? <Tabs label={t("title")} value={tab} onChange={setTab} tabs={tabs} /> : null}
    </RequirePermission>
  );
}

// ------------------------------------------------------------------ clinic profile

function ClinicTab() {
  const t = useTranslations("settings");
  const { settings, reloadSettings } = useClinic();
  if (!settings) return <LoadingBlock label={t("loading")} />;
  return <ClinicForm key={settings.rowVersion} settings={settings} onReload={reloadSettings} />;
}

function ClinicForm({ settings, onReload }: { settings: ClinicSettings; onReload: () => void }) {
  const t = useTranslations("settings.clinic");
  const tc = useTranslations("common");
  const toast = useToast();
  const { api } = useAuth();
  const form = useForm({
    name: settings.name,
    legalName: settings.legalName ?? "",
    vatNumber: settings.vatNumber ?? "",
    commercialRegistration: settings.commercialRegistration ?? "",
    email: settings.email ?? "",
    phone: toLocalMobile(settings.phone),
    city: settings.city ?? "",
    timeZone: settings.timeZone,
    locale: settings.locale,
    defaultAppointmentMinutes: String(settings.defaultAppointmentMinutes),
  });

  return (
    <form
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void form.submit(
          (v) => ({
            name: check(v.name, rules.required(), rules.maxLength(200)),
            legalName: check(v.legalName, rules.maxLength(200)),
            vatNumber: check(v.vatNumber, rules.vat()),
            commercialRegistration: v.commercialRegistration.trim() && !/^\d{10}$/.test(toLatinDigits(v.commercialRegistration.trim())) ? "invalid_value" : null,
            email: check(v.email, rules.email()),
            phone: check(v.phone, rules.saudiMobile()),
            city: check(v.city, rules.maxLength(100)),
            defaultAppointmentMinutes: check(v.defaultAppointmentMinutes, rules.required(), rules.range(5, 240)),
          }),
          async (v) => {
            await api.settings.update({
              name: v.name.trim(),
              legalName: emptyToNull(v.legalName),
              vatNumber: emptyToNull(v.vatNumber),
              commercialRegistration: emptyToNull(v.commercialRegistration),
              email: emptyToNull(v.email),
              phone: emptyToNull(v.phone),
              city: emptyToNull(v.city),
              timeZone: v.timeZone,
              locale: v.locale,
              defaultAppointmentMinutes: Number(toLatinDigits(v.defaultAppointmentMinutes)),
              rowVersion: settings.rowVersion,
            });
            toast({ tone: "success", title: tc("saved") });
            onReload();
          },
        );
      }}
    >
      <Card>
        <CardHeader title={t("title")} description={t("description")} />
        <CardBody className="flex flex-col gap-4">
          {form.formError?.isConcurrencyConflict ? (
            <ConflictAlert onReload={onReload} />
          ) : form.formError ? (
            <Alert tone="danger">{form.formError.message}</Alert>
          ) : null}
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
            <Input label={t("name")} required {...form.bind("name")} />
            <Input label={t("legalName")} {...form.bind("legalName")} />
            <Input label={t("slug")} value={settings.slug} readOnly disabled dir="ltr" hint={t("slugHint")} />
            <Input label={t("vatNumber")} dir="ltr" inputMode="numeric" maxLength={15} hint={t("vatHint")} {...form.bind("vatNumber")} />
            <Input label={t("commercialRegistration")} dir="ltr" inputMode="numeric" maxLength={10} {...form.bind("commercialRegistration")} />
            <Input label={t("city")} {...form.bind("city")} />
            <Input label={t("phone")} type="tel" dir="ltr" placeholder="05XXXXXXXX" {...form.bind("phone")} />
            <Input label={t("email")} type="email" dir="ltr" {...form.bind("email")} />
            <Select label={t("timeZone")} options={TIME_ZONES.map((z) => ({ value: z, label: z }))} {...form.bind("timeZone")} />
            <Select
              label={t("locale")}
              options={[
                { value: "ar", label: "العربية" },
                { value: "en", label: "English" },
              ]}
              {...form.bind("locale")}
            />
            <Input label={t("defaultAppointmentMinutes")} type="number" min={5} max={240} dir="ltr" {...form.bind("defaultAppointmentMinutes")} />
            <Input label={t("currency")} value={settings.currency} readOnly disabled dir="ltr" />
          </div>
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

// ------------------------------------------------------------------ branches

function BranchesTab() {
  const t = useTranslations("settings.branches");
  const toast = useToast();
  const { branches, branchScope, reloadBranches } = useClinic();
  const [editing, setEditing] = useState<Branch | "new" | null>(null);
  const canCreate = canCreateBranch(branchScope);

  return (
    <div className="flex flex-col gap-4">
      {canCreate ? (
        <div className="flex justify-end">
          <Button icon={<Plus className="size-4" aria-hidden="true" />} onClick={() => setEditing("new")}>
            {t("add")}
          </Button>
        </div>
      ) : (
        <Alert tone="info">{t("restrictedHint")}</Alert>
      )}
      {branches.length === 0 ? (
        <EmptyState icon={<Building2 className="size-6" />} title={t("empty")} />
      ) : (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 2xl:grid-cols-3">
          {branches.map((b) => (
            <Card key={b.id}>
              <CardHeader
                as="h3"
                title={b.name}
                description={b.city ?? undefined}
                actions={
                  canEditBranch(branchScope, b.id) ? (
                    <Button variant="ghost" size="icon" onClick={() => setEditing(b)} aria-label={t("editNamed", { name: b.name })}>
                      <Pencil className="size-4" aria-hidden="true" />
                    </Button>
                  ) : undefined
                }
              />
              <CardBody className="flex flex-col gap-2 text-sm">
                <div className="flex flex-wrap gap-2">
                  {b.isMain ? <Badge tone="primary">{t("main")}</Badge> : null}
                  {b.isActive ? <Badge tone="success">{t("active")}</Badge> : <Badge tone="neutral">{t("inactive")}</Badge>}
                </div>
                {b.address ? (
                  <p className="flex items-start gap-1.5 text-fg-muted">
                    <MapPin className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
                    {b.address}
                  </p>
                ) : null}
                {b.phone ? <p className="ltr-nums text-fg-muted">{toLocalMobile(b.phone)}</p> : null}
              </CardBody>
            </Card>
          ))}
        </div>
      )}
      {editing ? (
        <BranchDialog
          branch={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            toast({ tone: "success", title: t("saved") });
            reloadBranches();
          }}
        />
      ) : null}
    </div>
  );
}

function BranchDialog({ branch, onClose, onSaved }: { branch?: Branch; onClose: () => void; onSaved: () => void }) {
  const t = useTranslations("settings.branches");
  const tc = useTranslations("common");
  const { api } = useAuth();
  const form = useForm({
    name: branch?.name ?? "",
    city: branch?.city ?? "",
    address: branch?.address ?? "",
    phone: toLocalMobile(branch?.phone),
    isActive: branch?.isActive ?? true,
  });

  const save = () =>
    void form.submit(
      (v) => ({
        name: check(v.name, rules.required(), rules.maxLength(200)),
        city: check(v.city, rules.maxLength(100)),
        address: check(v.address, rules.maxLength(500)),
        phone: check(v.phone, rules.saudiMobile()),
      }),
      async (v) => {
        const body = {
          name: v.name.trim(),
          city: emptyToNull(v.city),
          address: emptyToNull(v.address),
          phone: emptyToNull(v.phone),
          latitude: branch?.latitude ?? null,
          longitude: branch?.longitude ?? null,
          isActive: v.isActive,
        };
        if (branch) await api.branches.update(branch.id, { ...body, rowVersion: branch.rowVersion });
        else await api.branches.create(body);
        onSaved();
      },
    );

  return (
    <Dialog
      open
      onClose={onClose}
      title={branch ? t("editNamed", { name: branch.name }) : t("add")}
      dismissible={!form.submitting}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={form.submitting}>
            {tc("cancel")}
          </Button>
          <Button onClick={save} loading={form.submitting}>
            {tc("save")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {form.formError && !form.formError.isUpgradeRequired ? <Alert tone="danger">{form.formError.message}</Alert> : null}
        <Input label={t("name")} required {...form.bind("name")} />
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Input label={t("city")} {...form.bind("city")} />
          <Input label={t("phone")} type="tel" dir="ltr" placeholder="05XXXXXXXX" {...form.bind("phone")} />
        </div>
        <Input label={t("address")} {...form.bind("address")} />
        {branch ? (
          <Checkbox
            label={t("isActive")}
            description={branch.isMain ? t("mainHint") : undefined}
            disabled={branch.isMain}
            checked={form.values.isActive}
            onChange={(e) => form.setField("isActive", e.target.checked)}
          />
        ) : null}
      </div>
    </Dialog>
  );
}

// ------------------------------------------------------------------ services

function ServicesTab() {
  const t = useTranslations("settings.services");
  const locale = useLocale();
  const toast = useToast();
  const { api } = useAuth();
  const { currency } = useClinic();
  const services = useQuery("services:all", () => api.services.list(false));
  const [editing, setEditing] = useState<ClinicService | "new" | null>(null);
  const [deleting, setDeleting] = useState<ClinicService | null>(null);
  const [busy, setBusy] = useState(false);

  const columns: Column<ClinicService>[] = [
    { id: "name", header: t("nameAr"), cell: (s) => <span className="font-medium">{s.nameAr}</span> },
    { id: "nameEn", header: t("nameEn"), hideable: true, cell: (s) => (s.nameEn ? <span dir="ltr">{s.nameEn}</span> : "—") },
    { id: "duration", header: t("duration"), cell: (s) => t("minutes", { count: s.durationMinutes }) },
    { id: "price", header: t("price"), cell: (s) => <span className="ltr-nums">{formatMoney(s.price, currency, locale)}</span> },
    {
      id: "status",
      header: t("status"),
      cell: (s) => (s.isActive ? <Badge tone="success">{t("active")}</Badge> : <Badge tone="neutral">{t("inactive")}</Badge>),
    },
  ];

  return (
    <Card>
      <DataTable
        caption={t("title")}
        columns={columns}
        rows={services.data}
        rowKey={(s) => s.id}
        loading={services.loading}
        error={services.error ? (isApiError(services.error) ? services.error.message : t("title")) : null}
        onRetry={services.reload}
        toolbar={
          <Button size="sm" icon={<Plus className="size-4" aria-hidden="true" />} onClick={() => setEditing("new")}>
            {t("add")}
          </Button>
        }
        rowActions={(s) => (
          <>
            <Button variant="ghost" size="icon" onClick={() => setEditing(s)} aria-label={t("editNamed", { name: s.nameAr })}>
              <Pencil className="size-4" aria-hidden="true" />
            </Button>
            <Button variant="ghost" size="icon" onClick={() => setDeleting(s)} aria-label={t("deleteNamed", { name: s.nameAr })}>
              <Trash2 className="size-4" aria-hidden="true" />
            </Button>
          </>
        )}
        empty={<EmptyState title={t("empty")} description={t("emptyHint")} />}
      />
      {editing ? (
        <ServiceDialog
          service={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            toast({ tone: "success", title: t("saved") });
            services.reload();
          }}
        />
      ) : null}
      <ConfirmDialog
        open={!!deleting}
        onClose={() => setDeleting(null)}
        title={t("deleteTitle")}
        description={deleting ? t("deleteDescription", { name: deleting.nameAr }) : undefined}
        confirmLabel={t("delete")}
        tone="danger"
        loading={busy}
        onConfirm={async () => {
          if (!deleting) return;
          setBusy(true);
          try {
            await api.services.remove(deleting.id);
            toast({ tone: "success", title: t("deleted") });
            services.reload();
          } catch (e) {
            toast({ tone: "danger", title: isApiError(e) ? e.message : t("deleteTitle") });
          } finally {
            setBusy(false);
            setDeleting(null);
          }
        }}
      />
    </Card>
  );
}

function ServiceDialog({ service, onClose, onSaved }: { service?: ClinicService; onClose: () => void; onSaved: () => void }) {
  const t = useTranslations("settings.services");
  const tc = useTranslations("common");
  const { api } = useAuth();
  const form = useForm({
    nameAr: service?.nameAr ?? "",
    nameEn: service?.nameEn ?? "",
    durationMinutes: String(service?.durationMinutes ?? 15),
    price: String(service?.price ?? 0),
    isActive: service?.isActive ?? true,
    isBookableOnline: service?.isBookableOnline ?? true,
  });

  const save = () =>
    void form.submit(
      (v) => ({
        nameAr: check(v.nameAr, rules.required(), rules.maxLength(200)),
        nameEn: check(v.nameEn, rules.maxLength(200)),
        durationMinutes: check(v.durationMinutes, rules.required(), rules.range(5, 480)),
        price: check(v.price, rules.required(), rules.range(0, 1_000_000)),
      }),
      async (v) => {
        const body = {
          nameAr: v.nameAr.trim(),
          nameEn: emptyToNull(v.nameEn),
          durationMinutes: Number(toLatinDigits(v.durationMinutes)),
          price: Number(toLatinDigits(v.price)),
          isActive: v.isActive,
          isBookableOnline: v.isBookableOnline,
        };
        if (service) await api.services.update(service.id, { ...body, rowVersion: service.rowVersion });
        else await api.services.create(body);
        onSaved();
      },
    );

  return (
    <Dialog
      open
      onClose={onClose}
      title={service ? t("editNamed", { name: service.nameAr }) : t("add")}
      dismissible={!form.submitting}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={form.submitting}>
            {tc("cancel")}
          </Button>
          <Button onClick={save} loading={form.submitting}>
            {tc("save")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {form.formError?.isConcurrencyConflict ? (
          <Alert tone="warning" title={tc("conflictTitle")}>{tc("conflictBody")}</Alert>
        ) : form.formError ? (
          <Alert tone="danger">{form.formError.message}</Alert>
        ) : null}
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Input label={t("nameAr")} required {...form.bind("nameAr")} />
          <Input label={t("nameEn")} dir="ltr" {...form.bind("nameEn")} />
          <Input label={t("duration")} type="number" min={5} max={480} dir="ltr" required {...form.bind("durationMinutes")} />
          <Input label={t("priceExVat")} type="number" min={0} step="0.01" dir="ltr" required {...form.bind("price")} />
        </div>
        <Checkbox label={t("isActive")} checked={form.values.isActive} onChange={(e) => form.setField("isActive", e.target.checked)} />
        <Checkbox
          label={t("isBookableOnline")}
          checked={form.values.isBookableOnline}
          onChange={(e) => form.setField("isBookableOnline", e.target.checked)}
        />
      </div>
    </Dialog>
  );
}
