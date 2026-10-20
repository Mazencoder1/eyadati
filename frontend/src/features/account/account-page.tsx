"use client";

import { MonitorSmartphone } from "lucide-react";
import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { PageHeader } from "@/components/app/page";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader, DescriptionList } from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/field";
import { Skeleton } from "@/components/ui/spinner";
import { useToast } from "@/components/ui/toast";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { SessionInfo } from "@/lib/api/types";
import { useAuth } from "@/lib/auth/session";
import { formatDateTime } from "@/lib/datetime";
import { useForm } from "@/lib/hooks/use-form";
import { useQuery } from "@/lib/hooks/use-query";
import { check, rules } from "@/lib/validation/saudi";
import { describeDevice } from "./account-model";

/** The signed-in user's own account: password and signed-in devices. Every member can open it (no permission). */
export function AccountPage() {
  const t = useTranslations("account");
  const { session } = useAuth();
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} />
      <div className="flex max-w-3xl flex-col gap-6">
        <Card>
          <CardHeader title={t("profile.title")} />
          <CardBody>
            <DescriptionList
              items={[
                { label: t("profile.name"), value: session?.user.fullName },
                { label: t("profile.email"), value: <span dir="ltr">{session?.user.email}</span> },
              ]}
            />
          </CardBody>
        </Card>
        <PasswordCard />
        <SessionsCard />
      </div>
    </>
  );
}

function PasswordCard() {
  const t = useTranslations("account.password");
  const toast = useToast();
  const { api } = useAuth();
  const empty = { currentPassword: "", newPassword: "", confirm: "" };
  const form = useForm(empty);

  return (
    <Card>
      <CardHeader title={t("title")} description={t("description")} />
      <CardBody>
        <form
          noValidate
          className="flex max-w-md flex-col gap-4"
          onSubmit={(e) => {
            e.preventDefault();
            void form
              .submit(
                (v) => ({
                  currentPassword: check(v.currentPassword, rules.required()),
                  newPassword:
                    check(v.newPassword, rules.password()) ??
                    (v.newPassword === v.currentPassword ? "same_password" : null),
                  confirm: v.confirm === v.newPassword ? null : "passwords_mismatch",
                }),
                async (v) => {
                  await api.auth.changePassword(v.currentPassword, v.newPassword);
                },
              )
              .then((ok) => {
                if (!ok) return;
                form.setValues(empty);
                toast({ tone: "success", title: t("done") });
              });
          }}
        >
          {form.formError && form.formError.code !== "INVALID_CREDENTIALS" ? (
            <Alert tone="danger">{form.formError.message}</Alert>
          ) : null}
          <Input
            label={t("current")}
            type="password"
            autoComplete="current-password"
            required
            {...form.bind("currentPassword")}
            // A wrong current password is a 401 answer, shown on the field it is about.
            error={
              form.errors.currentPassword ??
              (form.formError?.code === "INVALID_CREDENTIALS" ? form.formError.message : undefined)
            }
          />
          <Input
            label={t("new")}
            type="password"
            autoComplete="new-password"
            required
            hint={t("newHint")}
            {...form.bind("newPassword")}
          />
          <Input label={t("confirm")} type="password" autoComplete="new-password" required {...form.bind("confirm")} />
          <p className="text-xs text-fg-subtle">{t("signsOutOthers")}</p>
          <div>
            <Button type="submit" loading={form.submitting}>
              {t("submit")}
            </Button>
          </div>
        </form>
      </CardBody>
    </Card>
  );
}

function SessionsCard() {
  const t = useTranslations("account.sessions");
  const locale = useLocale();
  const toast = useToast();
  const { api, session } = useAuth();
  const { timeZone } = useClinic();
  // Keyed by the access token: a password change issues a new session and the list must reload.
  const sessions = useQuery(session ? `sessions:${session.accessToken}` : null, (signal) => api.auth.sessions(signal));
  const tenants = useQuery(session ? `tenants:${session.user.id}` : null, () => api.auth.tenants());
  const [ending, setEnding] = useState<SessionInfo | null>(null);
  const [busy, setBusy] = useState(false);

  const clinicName = (s: SessionInfo) =>
    s.tenantId ? (tenants.data?.find((x) => x.id === s.tenantId)?.name ?? t("otherClinic")) : t("platform");
  const deviceName = (s: SessionInfo) => {
    const { browser, os } = describeDevice(s.userAgent);
    return browser && os ? t("deviceOn", { browser, os }) : (browser ?? os ?? t("unknownDevice"));
  };
  const list = sessions.data ?? [];

  return (
    <Card>
      <CardHeader title={t("title")} description={t("description")} />
      <CardBody className="flex flex-col gap-4">
        {sessions.initialLoading ? (
          <div className="flex flex-col gap-3" aria-busy="true">
            <Skeleton className="h-14" />
            <Skeleton className="h-14" />
          </div>
        ) : sessions.error && sessions.data === undefined ? (
          <Alert
            tone="danger"
            action={
              <Button size="sm" variant="outline" onClick={sessions.reload}>
                {t("retry")}
              </Button>
            }
          >
            {isApiError(sessions.error) ? sessions.error.message : t("loadFailed")}
          </Alert>
        ) : list.length === 0 ? (
          <EmptyState icon={<MonitorSmartphone className="size-6" />} title={t("empty")} className="py-8" />
        ) : (
          <ul className="divide-y divide-border">
            {list.map((s) => (
              <li key={s.sessionId} className="flex flex-col gap-2 py-3 first:pt-0 last:pb-0 sm:flex-row sm:items-center sm:justify-between">
                <div className="min-w-0">
                  <p className="flex flex-wrap items-center gap-2 text-sm font-medium text-fg">
                    {deviceName(s)}
                    {s.isCurrent ? <Badge tone="primary">{t("current")}</Badge> : null}
                  </p>
                  <p className="mt-0.5 text-xs text-fg-muted">
                    {clinicName(s)} · {t("lastActive")} <span className="ltr-nums">{formatDateTime(s.lastActivityAtUtc, timeZone, locale)}</span>
                    {s.ipAddress ? (
                      <>
                        {" · "}
                        <span dir="ltr">{s.ipAddress}</span>
                      </>
                    ) : null}
                  </p>
                </div>
                {s.isCurrent ? null : (
                  <Button
                    size="sm"
                    variant="outline"
                    className="self-start sm:self-auto"
                    onClick={() => setEnding(s)}
                    aria-label={t("endLabel", { device: deviceName(s) })}
                  >
                    {t("end")}
                  </Button>
                )}
              </li>
            ))}
          </ul>
        )}
        <p className="text-xs text-fg-subtle">{t("note")}</p>
      </CardBody>

      {ending ? (
        <ConfirmDialog
          open
          onClose={() => setEnding(null)}
          title={t("confirmTitle")}
          description={t("confirmBody", { device: deviceName(ending) })}
          confirmLabel={t("end")}
          tone="danger"
          loading={busy}
          onConfirm={async () => {
            setBusy(true);
            try {
              await api.auth.revokeSession(ending.sessionId);
              toast({ tone: "success", title: t("ended") });
            } catch (e) {
              if (!isApiError(e)) throw e;
              toast({ tone: "danger", title: e.message });
            } finally {
              setBusy(false);
              setEnding(null);
              sessions.reload();
            }
          }}
        />
      ) : null}
    </Card>
  );
}
