"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/field";
import { isApiError } from "@/lib/api/client";
import type { MfaChallengeDetails, MfaEnrollmentDetails, TenantSummary } from "@/lib/api/types";
import { useAuth } from "@/lib/auth/session";
import { useForm } from "@/lib/hooks/use-form";
import { check, rules } from "@/lib/validation/saudi";
import { AuthCard, safeNextPath } from "./auth-card";
import { MfaChallengeStep, MfaEnrollmentStep } from "./mfa-steps";

/** Password first; a platform account then continues with its authenticator (no session exists until that succeeds). */
type Step = { kind: "password" } | { kind: "challenge"; token: string } | { kind: "enroll"; token: string };

export function LoginForm() {
  const t = useTranslations("auth.login");
  const router = useRouter();
  const params = useSearchParams();
  const { api, status } = useAuth();
  const next = safeNextPath(params.get("next"));
  const invitationNotice = params.get("notice") === "invitation";
  const form = useForm({ email: "", password: "" });
  const [tenants, setTenants] = useState<TenantSummary[] | null>(null);
  const [tenantId, setTenantId] = useState("");
  const [step, setStep] = useState<Step>({ kind: "password" });

  // Already signed in (refresh cookie still valid) → straight to the app.
  useEffect(() => {
    if (status === "authenticated") router.replace(next);
  }, [status, next, router]);

  const onSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    void form.submit(
      (v) => ({
        email: check(v.email, rules.required(), rules.email()),
        password: check(v.password, rules.required()),
        ...(tenants && !tenantId ? { tenantId: "required" as const } : {}),
      }),
      async (v) => {
        try {
          await api.auth.login({ email: v.email.trim(), password: v.password, tenantId: tenantId || undefined });
          router.replace(next);
        } catch (err) {
          // Platform accounts never get a session from the password alone: the API answers 409 with a short-lived token.
          if (isApiError(err) && err.code === "MFA_REQUIRED") {
            const token = (err.details as MfaChallengeDetails | undefined)?.challengeToken;
            if (token) {
              form.setField("password", "");
              setStep({ kind: "challenge", token });
              return;
            }
          }
          if (isApiError(err) && err.code === "MFA_ENROLLMENT_REQUIRED") {
            const token = (err.details as MfaEnrollmentDetails | undefined)?.enrollmentToken;
            if (token) {
              form.setField("password", "");
              setStep({ kind: "enroll", token });
              return;
            }
          }
          // A person who works at several clinics picks one; the API lists only clinics they belong to.
          if (err instanceof Error && "code" in err && err.code === "TENANT_SELECTION_REQUIRED") {
            const list = (err as { details?: unknown }).details;
            if (Array.isArray(list)) {
              setTenants(list as TenantSummary[]);
              return;
            }
          }
          throw err;
        }
      },
    );
  };

  const backToPassword = () => setStep({ kind: "password" });
  if (step.kind === "challenge") {
    return <MfaChallengeStep challengeToken={step.token} onSignedIn={() => router.replace(next)} onBack={backToPassword} />;
  }
  if (step.kind === "enroll") {
    return <MfaEnrollmentStep enrollmentToken={step.token} onSignedIn={() => router.replace(next)} onBack={backToPassword} />;
  }

  return (
    <AuthCard
      title={t("title")}
      description={t("subtitle")}
      footer={
        <>
          {t("noAccount")}{" "}
          <Link href="/register" className="font-medium text-primary hover:underline">
            {t("registerLink")}
          </Link>
        </>
      }
    >
      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
        {invitationNotice ? <Alert tone="info">{t("invitationNotice")}</Alert> : null}
        {form.formError ? <Alert tone="danger">{form.formError.message}</Alert> : null}
        <Input
          label={t("email")}
          type="email"
          autoComplete="username"
          inputMode="email"
          dir="ltr"
          required
          {...form.bind("email")}
        />
        <Input label={t("password")} type="password" autoComplete="current-password" required {...form.bind("password")} />

        {tenants ? (
          <fieldset className="flex flex-col gap-2 rounded-card border border-border p-3">
            <legend className="px-1 text-sm font-semibold">{t("chooseClinic")}</legend>
            {form.errors.tenantId ? (
              <p className="text-xs font-medium text-danger-strong" role="alert">
                {form.errors.tenantId}
              </p>
            ) : null}
            {tenants.map((tenant) => (
              <label
                key={tenant.id}
                className="flex cursor-pointer items-center gap-2 rounded-control px-2 py-2 text-sm hover:bg-surface-muted"
              >
                <input
                  type="radio"
                  name="tenant"
                  value={tenant.id}
                  checked={tenantId === tenant.id}
                  onChange={() => setTenantId(tenant.id)}
                  className="size-4 accent-[var(--c-primary)]"
                />
                {tenant.name}
              </label>
            ))}
          </fieldset>
        ) : null}

        <div className="flex justify-end">
          <Link href="/forgot-password" className="text-sm font-medium text-primary hover:underline">
            {t("forgot")}
          </Link>
        </div>
        <Button type="submit" size="lg" loading={form.submitting}>
          {t("submit")}
        </Button>
      </form>
    </AuthCard>
  );
}
