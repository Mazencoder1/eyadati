"use client";

import { MailPlus, Pencil, Plus, ShieldCheck, Trash2, UserCheck, UserPlus, UserX } from "lucide-react";
import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { PageHeader, RequirePermission } from "@/components/app/page";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader } from "@/components/ui/card";
import { DataTable, type Column } from "@/components/ui/data-table";
import { ConfirmDialog, Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Checkbox, Fieldset, Input, Select, Textarea } from "@/components/ui/field";
import { ActionMenu } from "@/components/ui/menu";
import { LoadingBlock } from "@/components/ui/spinner";
import { Tabs } from "@/components/ui/tabs";
import { useToast } from "@/components/ui/toast";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { PermissionGroup, Role, StaffMember } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { formatDateTime } from "@/lib/datetime";
import { useForm } from "@/lib/hooks/use-form";
import { useQuery } from "@/lib/hooks/use-query";
import { emptyToNull } from "@/lib/utils";
import { check, rules, toLocalMobile } from "@/lib/validation/saudi";
import { isSupportedPermission, supportedCatalog } from "./permission-visibility";
import { isBranchRestricted } from "@/lib/auth/branch-scope";
import { isSelf, seatView } from "./seat";

export function StaffPage() {
  const t = useTranslations("staff");
  const [tab, setTab] = useState("staff");
  const tabs = [
    { id: "staff", label: t("tabs.staff"), content: <StaffTab /> },
    { id: "roles", label: t("tabs.roles"), content: <RolesTab /> },
  ];
  return (
    <RequirePermission anyOf={[P.Staff.View]}>
      <PageHeader title={t("title")} description={t("subtitle")} />
      <Tabs label={t("title")} value={tab} onChange={setTab} tabs={tabs} />
    </RequirePermission>
  );
}

// ------------------------------------------------------------------ staff

function useRoles() {
  const { api } = useAuth();
  return useQuery("roles", () => api.roles.list());
}

function StaffTab() {
  const t = useTranslations("staff");
  const locale = useLocale();
  const toast = useToast();
  const { api, can, session } = useAuth();
  const { timeZone } = useClinic();
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState("");
  const [inviting, setInviting] = useState(false);
  const [editing, setEditing] = useState<StaffMember | null>(null);
  const list = useQuery(`staff:${page}:${search}:${status}`, (signal) =>
    api.staff.list({ page, pageSize: 20, search: search || undefined, status: status || undefined }, signal),
  );
  const canManage = can(P.Staff.Manage);

  const act = async (fn: () => Promise<unknown>, done: string) => {
    try {
      await fn();
      toast({ tone: "success", title: done });
      list.reload();
    } catch (e) {
      if (isApiError(e) && !e.isUpgradeRequired) toast({ tone: "danger", title: e.message });
    }
  };

  const [confirming, setConfirming] = useState<{ member: StaffMember; kind: "revoke" | "disable" } | null>(null);
  const [confirmBusy, setConfirmBusy] = useState(false);

  const statusBadge = (s: StaffMember) =>
    s.status === "Disabled" ? (
      <Badge tone="neutral" icon={<UserX className="size-3.5" />}>{t("statuses.Disabled")}</Badge>
    ) : s.status === "Invited" ? (
      <Badge tone="warning" icon={<MailPlus className="size-3.5" />}>{t("statuses.Invited")}</Badge>
    ) : (
      <Badge tone="success" icon={<UserCheck className="size-3.5" />}>{t("statuses.Active")}</Badge>
    );

  const columns: Column<StaffMember>[] = [
    {
      id: "name",
      header: t("fields.fullName"),
      cell: (s) => {
        const v = seatView(s);
        return (
          <div className="min-w-0">
            <p className="font-medium break-all" dir={v.pending ? "ltr" : undefined}>
              {v.title} {s.isOwner ? <Badge tone="primary" className="ms-1">{t("owner")}</Badge> : null}
            </p>
            {v.subtitle ? (
              <p className="truncate text-xs text-fg-muted" dir="ltr">
                {v.subtitle}
              </p>
            ) : (
              <p className="text-xs text-fg-muted">{t("pendingSeat")}</p>
            )}
          </div>
        );
      },
    },
    {
      id: "roles",
      header: t("fields.roles"),
      cell: (s) => (
        <div className="flex flex-wrap justify-end gap-1 md:justify-start">
          {s.roles.map((r) => (
            <Badge key={r.id} tone="info">{r.name}</Badge>
          ))}
        </div>
      ),
    },
    { id: "phone", header: t("fields.phone"), hideable: true, defaultHidden: true, cell: (s) => <span className="ltr-nums">{toLocalMobile(s.phoneNumber)}</span> },
    { id: "status", header: t("fields.status"), cell: statusBadge },
    {
      id: "lastLogin",
      header: t("fields.lastLogin"),
      hideable: true,
      cell: (s) => (s.lastLoginAtUtc ? <span className="tabular-nums">{formatDateTime(s.lastLoginAtUtc, timeZone, locale)}</span> : "—"),
    },
  ];

  return (
    <Card>
      <DataTable
        caption={t("tabs.staff")}
        columns={columns}
        rows={list.data?.items}
        rowKey={(s) => s.membershipId}
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
        toolbar={
          <>
            <Select
              label={t("fields.status")}
              hideLabel
              value={status}
              onChange={(e) => {
                setStatus(e.target.value);
                setPage(1);
              }}
              placeholder={t("allStatuses")}
              options={(["Active", "Invited", "Disabled"] as const).map((s) => ({ value: s, label: t(`statuses.${s}`) }))}
              className="w-48"
            />
            {canManage ? (
              <Button size="sm" icon={<UserPlus className="size-4" aria-hidden="true" />} onClick={() => setInviting(true)}>
                {t("invite")}
              </Button>
            ) : null}
          </>
        }
        rowActions={
          canManage
            ? (s) => {
                if (s.isOwner || isSelf(s, session?.user.id)) return null;
                const v = seatView(s);
                return (
                  <ActionMenu
                    label={t("actionsFor", { name: v.title })}
                    actions={[
                      { id: "edit", label: t("editAccess"), icon: <Pencil className="size-4" />, onSelect: () => setEditing(s) },
                      {
                        id: "resend",
                        label: t("resendInvitation"),
                        icon: <MailPlus className="size-4" />,
                        disabled: !v.canResend,
                        onSelect: () => void act(() => api.staff.resendInvitation(s.membershipId), t("invitationResent")),
                      },
                      v.canReinvite
                        ? {
                            id: "enable",
                            label: t("reinvite"),
                            icon: <UserCheck className="size-4" />,
                            onSelect: () => void act(() => api.staff.enable(s.membershipId), t("reinvited")),
                          }
                        : {
                            id: "disable",
                            label: v.disableKind === "revoke" ? t("revokeInvitation") : t("disable"),
                            icon: <UserX className="size-4" />,
                            tone: "danger",
                            onSelect: () => setConfirming({ member: s, kind: v.disableKind === "revoke" ? "revoke" : "disable" }),
                          },
                    ]}
                  />
                );
              }
            : undefined
        }
        empty={<EmptyState title={t("emptyTitle")} />}
      />
      {confirming ? (
        <ConfirmDialog
          open
          onClose={() => setConfirming(null)}
          title={confirming.kind === "revoke" ? t("revokeInvitation") : t("disable")}
          description={
            confirming.kind === "revoke"
              ? t("revokeDescription", { email: confirming.member.email })
              : t("disableDescription", { name: confirming.member.fullName })
          }
          confirmLabel={confirming.kind === "revoke" ? t("revokeInvitation") : t("disable")}
          tone="danger"
          loading={confirmBusy}
          onConfirm={async () => {
            setConfirmBusy(true);
            await act(
              () => api.staff.disable(confirming.member.membershipId),
              confirming.kind === "revoke" ? t("invitationRevoked") : t("disabled"),
            );
            setConfirmBusy(false);
            setConfirming(null);
          }}
        />
      ) : null}
      {inviting ? (
        <StaffDialog
          onClose={() => setInviting(false)}
          onSaved={() => {
            setInviting(false);
            toast({ tone: "success", title: t("invited") });
            list.reload();
          }}
        />
      ) : null}
      {editing ? (
        <StaffDialog
          member={editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            toast({ tone: "success", title: t("updated") });
            list.reload();
          }}
        />
      ) : null}
    </Card>
  );
}

/** Invite (new member) or edit roles/branches (existing member). Roles the actor can't grant are rejected server-side. */
function StaffDialog({ member, onClose, onSaved }: { member?: StaffMember; onClose: () => void; onSaved: () => void }) {
  const t = useTranslations("staff");
  const tc = useTranslations("common");
  const { api } = useAuth();
  const { branches, allowedBranches, branchScope } = useClinic();
  const restricted = isBranchRestricted(branchScope);
  // A branch-restricted manager may only grant branches inside their own scope, and must pick at least one (T-009).
  const branchOptions = restricted ? allowedBranches : branches;
  const roles = useRoles();
  const assignable = (roles.data ?? []).filter((r) => !r.isProtected);
  const [roleIds, setRoleIds] = useState<string[]>(member?.roles.map((r) => r.id) ?? []);
  const [branchIds, setBranchIds] = useState<string[]>(member?.branchIds ?? []);
  const form = useForm({ fullName: "", email: "", phoneNumber: "" });
  const isEdit = !!member;

  const toggle = (list: string[], id: string) => (list.includes(id) ? list.filter((x) => x !== id) : [...list, id]);

  const save = () =>
    void form.submit(
      (v) => ({
        ...(isEdit
          ? {}
          : {
              fullName: check(v.fullName, rules.required(), rules.maxLength(200)),
              email: check(v.email, rules.required(), rules.email()),
              phoneNumber: check(v.phoneNumber, rules.saudiMobile()),
            }),
        roleIds: roleIds.length ? null : "required",
        branchIds: restricted && branchIds.length === 0 ? "required" : null,
      }),
      async (v) => {
        if (isEdit) await api.staff.update(member.membershipId, { roleIds, branchIds, rowVersion: member.rowVersion });
        else
          await api.staff.invite({
            fullName: v.fullName.trim(),
            email: v.email.trim(),
            phoneNumber: emptyToNull(v.phoneNumber),
            roleIds,
            branchIds,
          });
        onSaved();
      },
    );

  return (
    <Dialog
      open
      onClose={onClose}
      size="lg"
      title={isEdit ? t("editTitle", { name: seatView(member).title }) : t("inviteTitle")}
      description={isEdit ? undefined : t("inviteDescription")}
      dismissible={!form.submitting}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={form.submitting}>
            {tc("cancel")}
          </Button>
          <Button onClick={save} loading={form.submitting}>
            {isEdit ? tc("saveChanges") : t("sendInvitation")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {form.formError?.isConcurrencyConflict ? (
          <Alert tone="warning" title={tc("conflictTitle")}>{tc("conflictBody")}</Alert>
        ) : form.formError && !form.formError.isUpgradeRequired ? (
          <Alert tone="danger">{form.formError.message}</Alert>
        ) : null}
        {!isEdit ? (
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <Input label={t("fields.fullName")} required {...form.bind("fullName")} />
            <Input label={t("fields.email")} required type="email" dir="ltr" {...form.bind("email")} />
            <Input label={t("fields.phone")} type="tel" dir="ltr" placeholder="05XXXXXXXX" {...form.bind("phoneNumber")} />
          </div>
        ) : null}
        <Fieldset legend={t("fields.roles")}>
          {form.errors.roleIds ? <p className="mb-2 text-xs font-medium text-danger-strong">{form.errors.roleIds}</p> : null}
          {roles.loading ? (
            <LoadingBlock label={tc("loading")} className="py-4" />
          ) : (
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
              {assignable.map((r) => (
                <Checkbox
                  key={r.id}
                  label={r.name}
                  description={r.description ?? undefined}
                  checked={roleIds.includes(r.id)}
                  onChange={() => setRoleIds((l) => toggle(l, r.id))}
                />
              ))}
            </div>
          )}
        </Fieldset>
        {branches.length > 1 || restricted ? (
          <Fieldset legend={t("fields.branches")}>
            <p className="mb-2 text-xs text-fg-subtle">{restricted ? t("branchesHintRestricted") : t("branchesHint")}</p>
            {form.errors.branchIds ? <p className="mb-2 text-xs font-medium text-danger-strong">{form.errors.branchIds}</p> : null}
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
              {branchOptions.map((b) => (
                <Checkbox key={b.id} label={b.name} checked={branchIds.includes(b.id)} onChange={() => setBranchIds((l) => toggle(l, b.id))} />
              ))}
            </div>
          </Fieldset>
        ) : null}
      </div>
    </Dialog>
  );
}

// ------------------------------------------------------------------ roles

function RolesTab() {
  const t = useTranslations("staff.roles");
  const toast = useToast();
  const { api, can } = useAuth();
  const roles = useRoles();
  const canManage = can(P.Roles.Manage);
  const catalog = useQuery(canManage ? "permissions-catalog" : null, () => api.roles.catalog());
  const [editing, setEditing] = useState<Role | "new" | null>(null);
  const [deleting, setDeleting] = useState<Role | null>(null);
  const [busy, setBusy] = useState(false);
  const tp = usePermissionLabel();

  if (roles.initialLoading) return <LoadingBlock label={t("loading")} />;
  if (roles.error && !roles.data) return <Alert tone="danger">{isApiError(roles.error) ? roles.error.message : t("loading")}</Alert>;

  return (
    <div className="flex flex-col gap-4">
      {canManage ? (
        <div className="flex justify-end">
          <Button icon={<Plus className="size-4" aria-hidden="true" />} onClick={() => setEditing("new")}>
            {t("new")}
          </Button>
        </div>
      ) : null}
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 2xl:grid-cols-3">
        {(roles.data ?? []).map((r) => (
          <Card key={r.id}>
            <CardHeader
              as="h3"
              title={
                <span className="flex items-center gap-2">
                  <ShieldCheck className="size-5 text-primary" aria-hidden="true" />
                  {r.name}
                </span>
              }
              description={r.description ?? undefined}
              actions={
                canManage && !r.isProtected ? (
                  <>
                    <Button variant="ghost" size="icon" onClick={() => setEditing(r)} aria-label={t("editNamed", { name: r.name })}>
                      <Pencil className="size-4" aria-hidden="true" />
                    </Button>
                    {!r.systemKey ? (
                      <Button variant="ghost" size="icon" onClick={() => setDeleting(r)} aria-label={t("deleteNamed", { name: r.name })}>
                        <Trash2 className="size-4" aria-hidden="true" />
                      </Button>
                    ) : null}
                  </>
                ) : null
              }
            />
            <CardBody className="flex flex-col gap-3 text-sm">
              <div className="flex flex-wrap gap-2">
                {r.systemKey ? <Badge tone="primary">{t("system")}</Badge> : <Badge tone="neutral">{t("custom")}</Badge>}
                <Badge tone="info">{t("members", { count: r.memberCount })}</Badge>
                <Badge tone="neutral">{t("permissionsCount", { count: r.permissions.filter(isSupportedPermission).length })}</Badge>
              </div>
              <p className="line-clamp-3 text-fg-muted">{r.permissions.filter(isSupportedPermission).map(tp).join("، ")}</p>
            </CardBody>
          </Card>
        ))}
      </div>

      {editing && catalog.data ? (
        <RoleDialog
          role={editing === "new" ? undefined : editing}
          catalog={supportedCatalog(catalog.data)}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            toast({ tone: "success", title: t("saved") });
            roles.reload();
          }}
        />
      ) : null}

      <ConfirmDialog
        open={!!deleting}
        onClose={() => setDeleting(null)}
        title={t("deleteTitle")}
        description={deleting ? t("deleteDescription", { name: deleting.name }) : undefined}
        confirmLabel={t("delete")}
        tone="danger"
        loading={busy}
        onConfirm={async () => {
          if (!deleting) return;
          setBusy(true);
          try {
            await api.roles.remove(deleting.id);
            toast({ tone: "success", title: t("deleted") });
            roles.reload();
          } catch (e) {
            toast({ tone: "danger", title: isApiError(e) ? e.message : t("deleteTitle") });
          } finally {
            setBusy(false);
            setDeleting(null);
          }
        }}
      />
    </div>
  );
}

/** Localized permission label, falling back to the raw name for permissions added later on the server. */
function usePermissionLabel() {
  const t = useTranslations("permissions");
  return (permission: string) => (t.has(permission as never) ? t(permission as never) : permission);
}

function RoleDialog({
  role,
  catalog,
  onClose,
  onSaved,
}: {
  role?: Role;
  catalog: PermissionGroup[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useTranslations("staff.roles");
  const tc = useTranslations("common");
  const tg = useTranslations("permissionGroups");
  const tp = usePermissionLabel();
  const { api, can } = useAuth();
  const form = useForm({ name: role?.name ?? "", description: role?.description ?? "" });
  const [selected, setSelected] = useState<Set<string>>(new Set(role?.permissions ?? []));

  const toggle = (p: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(p)) next.delete(p);
      else next.add(p);
      return next;
    });

  const save = () =>
    void form.submit(
      (v) => ({ name: check(v.name, rules.required(), rules.maxLength(100)), description: check(v.description, rules.maxLength(500)) }),
      async (v) => {
        // `selected` starts from the role's stored permissions, so permissions hidden from the editor (unsupported
        // features) are sent back unchanged: hiding them never edits stored role data.
        const body = { name: v.name.trim(), description: emptyToNull(v.description), permissions: [...selected] };
        if (role) await api.roles.update(role.id, { ...body, rowVersion: role.rowVersion });
        else await api.roles.create(body);
        onSaved();
      },
    );

  return (
    <Dialog
      open
      onClose={onClose}
      size="xl"
      title={role ? t("editNamed", { name: role.name }) : t("new")}
      description={t("escalationHint")}
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
          <Input label={t("name")} required {...form.bind("name")} />
          <Textarea label={t("description")} rows={1} {...form.bind("description")} />
        </div>
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-3">
          {catalog.map((group) => (
            <Fieldset key={group.group} legend={tg.has(group.group as never) ? tg(group.group as never) : group.group}>
              <div className="flex flex-col gap-2">
                {group.permissions.map((p) => (
                  <Checkbox
                    key={p}
                    label={tp(p)}
                    checked={selected.has(p)}
                    // Can't grant (or revoke) what you don't hold yourself — the API enforces the same rule.
                    disabled={!can(p)}
                    onChange={() => toggle(p)}
                  />
                ))}
              </div>
            </Fieldset>
          ))}
        </div>
      </div>
    </Dialog>
  );
}
