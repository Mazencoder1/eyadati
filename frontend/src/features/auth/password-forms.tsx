"use client";

import { CheckCircle2, MailCheck } from "lucide-react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button, ButtonLink } from "@/components/ui/button";
import { Input } from "@/components/ui/field";
import { LoadingBlock } from "@/components/ui/spinner";
import { isApiError } from "@/lib/api/client";
import { useAuth } from "@/lib/auth/session";
import { useOneTimeToken } from "@/lib/auth/one-time-token";
import { useForm } from "@/lib/hooks/use-form";
import { HOME_PATH } from "@/lib/routes";
import { check, rules } from "@/lib/validation/saudi";
import { AuthCard } from "./auth-card";

export function ForgotPasswordForm() {
  const t = useTranslations("auth.forgot");
  const { api } = useAuth();
  const form = useForm({ email: "" });
  const [sent, setSent] = useState(false);

  if (sent) {
    return (
      <AuthCard title={t("sentTitle")}>
        <Alert tone="success" title={t("sentTitle")}>
          {t("sentBody")}
        </Alert>
        <ButtonLink href="/login" variant="outline" className="mt-6 w-full">
          {t("backToLogin")}
        </ButtonLink>
      </AuthCard>
    );
  }

  return (
    <AuthCard
      title={t("title")}
      description={t("subtitle")}
      footer={
        <Link href="/login" className="font-medium text-primary hover:underline">
          {t("backToLogin")}
        </Link>
      }
    >
      <form
        noValidate
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          void form.submit(
            (v) => ({ email: check(v.email, rules.required(), rules.email()) }),
            async (v) => {
              await api.auth.forgotPassword(v.email.trim());
              setSent(true);
            },
          );
        }}
      >
        {form.formError ? <Alert tone="danger">{form.formError.message}</Alert> : null}
        <Input label={t("email")} type="email" dir="ltr" autoComplete="email" required {...form.bind("email")} />
        <Button type="submit" size="lg" loading={form.submitting}>
          {t("submit")}
        </Button>
      </form>
    </AuthCard>
  );
}

/** "Reset password" from the emailed link. (Invitations have their own flow: accept-invitation.tsx.) */
export function SetPasswordForm() {
  const t = useTranslations("auth.reset");
  const tc = useTranslations("auth.setPassword");
  const { api } = useAuth();
  const { token, ready } = useOneTimeToken();
  const form = useForm({ password: "", confirm: "" });
  const [done, setDone] = useState(false);

  if (!ready) {
    return (
      <AuthCard title={t("title")}>
        <LoadingBlock label={t("title")} className="py-8" />
      </AuthCard>
    );
  }

  if (!token) {
    return (
      <AuthCard title={t("title")}>
        <Alert tone="danger">{tc("missingToken")}</Alert>
      </AuthCard>
    );
  }

  if (done) {
    return (
      <AuthCard title={t("doneTitle")}>
        <Alert tone="success">{t("doneBody")}</Alert>
        <ButtonLink href="/login" className="mt-6 w-full">
          {tc("goToLogin")}
        </ButtonLink>
      </AuthCard>
    );
  }

  return (
    <AuthCard title={t("title")} description={t("subtitle")}>
      <form
        noValidate
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          void form.submit(
            (v) => ({
              password: check(v.password, rules.password()),
              confirm: v.confirm === v.password ? null : "passwords_mismatch",
            }),
            async (v) => {
              await api.auth.resetPassword(token, v.password);
              setDone(true);
            },
          );
        }}
      >
        {form.formError ? <Alert tone="danger">{form.formError.message}</Alert> : null}
        <Input
          label={tc("password")}
          type="password"
          autoComplete="new-password"
          required
          hint={tc("passwordHint")}
          {...form.bind("password")}
          // Server reports this field as "newPassword".
          error={form.errors.password ?? form.errors.newPassword}
        />
        <Input label={tc("confirm")} type="password" autoComplete="new-password" required {...form.bind("confirm")} />
        <Button type="submit" size="lg" loading={form.submitting}>
          {t("submit")}
        </Button>
      </form>
    </AuthCard>
  );
}

export function VerifyEmail() {
  const t = useTranslations("auth.verify");
  const { api } = useAuth();
  const { token, ready } = useOneTimeToken();
  const [result, setResult] = useState<{ ok: boolean; message?: string } | null>(null);

  useEffect(() => {
    if (!token) return;
    let active = true;
    api.auth.verifyEmail(token).then(
      () => active && setResult({ ok: true }),
      (e: unknown) => active && setResult({ ok: false, message: isApiError(e) ? e.message : undefined }),
    );
    return () => {
      active = false;
    };
  }, [api, token]);

  if (!ready) {
    return (
      <AuthCard title={t("title")}>
        <LoadingBlock label={t("verifying")} className="py-8" />
      </AuthCard>
    );
  }

  if (!token) {
    return (
      <AuthCard title={t("title")}>
        <Alert tone="danger">{t("missingToken")}</Alert>
      </AuthCard>
    );
  }

  return (
    <AuthCard title={t("title")}>
      {result === null ? (
        <LoadingBlock label={t("verifying")} className="py-8" />
      ) : result.ok ? (
        <div className="flex flex-col items-center gap-3 text-center">
          <CheckCircle2 className="size-12 text-success-strong" aria-hidden="true" />
          <p role="status" className="text-fg">
            {t("success")}
          </p>
          <ButtonLink href={HOME_PATH} className="mt-2 w-full">
            {t("continue")}
          </ButtonLink>
        </div>
      ) : (
        <div className="flex flex-col gap-4">
          <Alert tone="danger">{result.message ?? t("failed")}</Alert>
          <ButtonLink href="/login" variant="outline" icon={<MailCheck className="size-4" aria-hidden="true" />}>
            {t("backToLogin")}
          </ButtonLink>
        </div>
      )}
    </AuthCard>
  );
}
