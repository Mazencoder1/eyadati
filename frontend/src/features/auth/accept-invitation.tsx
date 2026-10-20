"use client";

import { Building2, LogIn, LogOut } from "lucide-react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button, ButtonLink } from "@/components/ui/button";
import { Input } from "@/components/ui/field";
import { LoadingBlock } from "@/components/ui/spinner";
import { isApiError } from "@/lib/api/client";
import type { TenantSummary } from "@/lib/api/types";
import { useAuth } from "@/lib/auth/session";
import { useForm } from "@/lib/hooks/use-form";
import { HOME_PATH } from "@/lib/routes";
import { check, rules } from "@/lib/validation/saudi";
import { AuthCard } from "./auth-card";
import { classifyAcceptError, invitationLoginHref } from "./invitation";

type Phase =
  | { kind: "ready" }
  /** Signed in, but the invitation is for an account without a password yet (a new account). */
  | { kind: "password" }
  | { kind: "wrong-account"; message: string }
  | { kind: "invalid"; message: string }
  | { kind: "done-new" }
  | { kind: "done-existing"; clinics: TenantSummary[] | null };

/**
 * Accept a staff invitation (T-010). The person consents explicitly by pressing the button:
 * - signed in → "accept as <email>" (an existing account joins; its password is never touched)
 * - signed out → set a password (new account); an existing account is sent to sign in and comes back here with the
 *   same token in the internal return path.
 * Whether an email is registered is never inferred here beyond what the server answers.
 */
export function AcceptInvitation() {
  const t = useTranslations("auth.invitation");
  const tc = useTranslations("auth.setPassword");
  const router = useRouter();
  const { api, status, session, logout } = useAuth();
  const token = useSearchParams().get("token") ?? "";
  const [phase, setPhase] = useState<Phase>({ kind: "ready" });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const form = useForm({ password: "", confirm: "" });

  if (!token) {
    return (
      <AuthCard title={t("title")}>
        <Alert tone="danger">{tc("missingToken")}</Alert>
      </AuthCard>
    );
  }
  if (status === "loading") return <LoadingBlock label={t("checkingSession")} className="py-16" />;

  const signInAsInvited = async () => {
    setBusy(true);
    try {
      await logout();
    } finally {
      setBusy(false);
      router.replace(invitationLoginHref(token));
    }
  };

  const loadClinics = async () => {
    try {
      return await api.auth.tenants();
    } catch {
      return null;
    }
  };

  /** Signed in: accept for the current account (no password sent). */
  const acceptSignedIn = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.auth.acceptInvitation(token);
      setPhase({ kind: "done-existing", clinics: await loadClinics() });
    } catch (e) {
      const outcome = classifyAcceptError(e);
      const message = isApiError(e) ? e.message : t("failed");
      if (outcome === "password-required") setPhase({ kind: "password" });
      else if (outcome === "sign-in-required") setPhase({ kind: "wrong-account", message });
      else if (outcome === "invalid-link") setPhase({ kind: "invalid", message });
      else setError(message);
    } finally {
      setBusy(false);
    }
  };

  /** New account: set the first password. */
  const acceptWithPassword = () =>
    void form.submit(
      (v) => ({
        password: check(v.password, rules.password()),
        confirm: v.confirm === v.password ? null : "passwords_mismatch",
      }),
      async (v) => {
        try {
          await api.auth.acceptInvitation(token, v.password);
          setPhase({ kind: "done-new" });
        } catch (e) {
          const outcome = classifyAcceptError(e);
          if (outcome === "sign-in-required") {
            // The invited email already has an account: sign in to it, then accept here again (same token).
            router.replace(invitationLoginHref(token));
            return;
          }
          if (outcome === "invalid-link") {
            setPhase({ kind: "invalid", message: isApiError(e) ? e.message : t("failed") });
            return;
          }
          throw e;
        }
      },
    );

  if (phase.kind === "invalid") {
    return (
      <AuthCard title={t("invalidTitle")}>
        <Alert tone="danger" title={phase.message}>
          {t("invalidBody")}
        </Alert>
        <ButtonLink href={status === "authenticated" ? HOME_PATH : "/login"} variant="outline" className="mt-6 w-full">
          {status === "authenticated" ? t("goHome") : tc("goToLogin")}
        </ButtonLink>
      </AuthCard>
    );
  }

  if (phase.kind === "done-new") {
    return (
      <AuthCard title={t("doneTitle")}>
        <Alert tone="success">{t("doneBody")}</Alert>
        <ButtonLink href="/login" className="mt-6 w-full">
          {tc("goToLogin")}
        </ButtonLink>
      </AuthCard>
    );
  }

  if (phase.kind === "done-existing") {
    const others = (phase.clinics ?? []).filter((c) => c.id !== session?.tenant?.id);
    return (
      <AuthCard title={t("joinedTitle")}>
        <Alert tone="success">{t("joinedBody")}</Alert>
        {others.length > 0 ? (
          <div className="mt-6 flex flex-col gap-2">
            <p className="text-sm font-medium text-fg">{t("openClinic")}</p>
            {others.map((c) => (
              <Button
                key={c.id}
                variant="outline"
                className="justify-start"
                loading={busy}
                icon={<Building2 className="size-4" aria-hidden="true" />}
                onClick={async () => {
                  setBusy(true);
                  try {
                    await api.auth.switchTenant(c.id);
                    router.replace(HOME_PATH);
                  } catch (e) {
                    setError(isApiError(e) ? e.message : t("failed"));
                    setBusy(false);
                  }
                }}
              >
                {c.name}
              </Button>
            ))}
            {error ? <Alert tone="danger">{error}</Alert> : null}
          </div>
        ) : (
          <ButtonLink href={HOME_PATH} className="mt-6 w-full">
            {t("goHome")}
          </ButtonLink>
        )}
      </AuthCard>
    );
  }

  if (status === "authenticated" && phase.kind !== "password") {
    const email = session?.user.email ?? "";
    return (
      <AuthCard title={t("title")} description={t("signedInSubtitle")}>
        <div className="flex flex-col gap-4">
          <div className="text-sm text-fg">
            <p>{t("signedInAs")}</p>
            <p className="mt-0.5 w-fit max-w-full font-semibold break-all" dir="ltr">
              {email}
            </p>
          </div>
          {phase.kind === "wrong-account" ? (
            <Alert tone="warning" title={phase.message}>
              {t("wrongAccountBody")}
            </Alert>
          ) : null}
          {error ? <Alert tone="danger">{error}</Alert> : null}
          {phase.kind === "wrong-account" ? (
            <Button size="lg" loading={busy} icon={<LogIn className="size-4" aria-hidden="true" />} onClick={() => void signInAsInvited()}>
              {t("switchAccount")}
            </Button>
          ) : (
            <>
              <Button size="lg" loading={busy} onClick={() => void acceptSignedIn()}>
                {t("acceptAs")}
              </Button>
              <Button
                variant="ghost"
                disabled={busy}
                icon={<LogOut className="size-4 rtl:rotate-180" aria-hidden="true" />}
                onClick={() => void signInAsInvited()}
              >
                {t("notYou")}
              </Button>
            </>
          )}
        </div>
      </AuthCard>
    );
  }

  return (
    <AuthCard
      title={t("title")}
      description={t("subtitle")}
      footer={
        status === "anonymous" ? (
          <>
            {t("haveAccount")}{" "}
            <Link href={invitationLoginHref(token)} className="font-medium text-primary hover:underline">
              {t("signInToAccept")}
            </Link>
          </>
        ) : undefined
      }
    >
      <form
        noValidate
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          acceptWithPassword();
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
