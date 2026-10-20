"use client";

import { Copy, ExternalLink } from "lucide-react";
import { useTranslations } from "next-intl";
import { useEffect, useRef, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/field";
import { Spinner } from "@/components/ui/spinner";
import { isApiError } from "@/lib/api/client";
import type { MfaEnrollmentResult, MfaSetup } from "@/lib/api/types";
import { useAuth } from "@/lib/auth/session";
import { useForm } from "@/lib/hooks/use-form";
import { check, rules } from "@/lib/validation/saudi";
import { AuthCard } from "./auth-card";
import { RecoveryCodes } from "./recovery-codes";

/** What a person types into a code box: spaces are noise (the authenticator shows "123 456"). */
const compact = (value: string) => value.replace(/\s+/g, "");

/**
 * Second step of sign-in for a platform account (409 MFA_REQUIRED): a TOTP from the authenticator app, or one of the
 * one-time recovery codes. No session exists until this succeeds; the API sets it (and the httpOnly cookie) only then.
 */
export function MfaChallengeStep({
  challengeToken,
  onSignedIn,
  onBack,
}: {
  challengeToken: string;
  onSignedIn: () => void;
  onBack: () => void;
}) {
  const t = useTranslations("auth.mfa.challenge");
  const { api } = useAuth();
  const [useRecovery, setUseRecovery] = useState(false);
  const form = useForm({ code: "" });

  const onSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    void form
      .submit(
        (v) => ({ code: check(v.code, rules.required()) }),
        async (v) => {
          try {
            await api.auth.mfa.verify(challengeToken, useRecovery ? { recoveryCode: v.code.trim() } : { totpCode: compact(v.code) });
            onSignedIn();
          } catch (e) {
            // A wrong code is not a reason to keep it on screen. Cleared here, in the same step that reports the
            // error, so the box is empty by the time the message is visible.
            form.setField("code", "");
            throw e;
          }
        },
      );
  };

  return (
    <AuthCard title={t("title")} description={t("subtitle")}>
      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-4">
        {form.formError ? <Alert tone="danger">{form.formError.message}</Alert> : null}
        <Input
          key={useRecovery ? "recovery" : "totp"}
          label={useRecovery ? t("recoveryCode") : t("code")}
          hint={useRecovery ? t("recoveryHint") : t("codeHint")}
          dir="ltr"
          autoFocus
          required
          autoComplete="one-time-code"
          inputMode={useRecovery ? "text" : "numeric"}
          autoCapitalize={useRecovery ? "characters" : "off"}
          spellCheck={false}
          {...form.bind("code")}
        />
        <Button type="submit" size="lg" loading={form.submitting}>
          {t("submit")}
        </Button>
        <div className="flex flex-col items-start gap-2 text-sm">
          <button
            type="button"
            className="font-medium text-primary hover:underline"
            onClick={() => {
              setUseRecovery((v) => !v);
              form.setField("code", "");
              form.setFormError(null);
            }}
          >
            {useRecovery ? t("useApp") : t("useRecovery")}
          </button>
          <button type="button" className="text-fg-muted hover:underline" onClick={onBack}>
            {t("back")}
          </button>
        </div>
      </form>
    </AuthCard>
  );
}

/**
 * Enrolment for a platform account without an authenticator (409 MFA_ENROLLMENT_REQUIRED), in two screens:
 * 1. the setup key / link, shown once, and a first code that proves the app works;
 * 2. the 10 recovery codes, shown once. The session the API returned is only adopted when the person continues, so
 *    the sign-in page cannot redirect away before the codes were seen.
 * The secret lives in component state only (never storage, URL or logs).
 */
export function MfaEnrollmentStep({
  enrollmentToken,
  onSignedIn,
  onBack,
}: {
  enrollmentToken: string;
  onSignedIn: () => void;
  onBack: () => void;
}) {
  const t = useTranslations("auth.mfa.enroll");
  const tc = useTranslations("auth.mfa.codes");
  const { api } = useAuth();
  const [setup, setSetup] = useState<MfaSetup | null>(null);
  const [startError, setStartError] = useState<string | null>(null);
  const [done, setDone] = useState<MfaEnrollmentResult | null>(null);
  const [saved, setSaved] = useState(false);
  const started = useRef(false);
  const form = useForm({ code: "" });

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    api.auth.mfa
      .enrollStart(enrollmentToken)
      .then(setSetup)
      .catch((e: unknown) => setStartError(isApiError(e) ? e.message : t("failed")));
  }, [api, enrollmentToken, t]);

  if (done) {
    return (
      <AuthCard title={tc("title")} description={tc("subtitle")}>
        <div className="flex flex-col gap-4">
          <Alert tone="warning">{tc("warning")}</Alert>
          <RecoveryCodes codes={done.recoveryCodes} />
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={saved}
              onChange={(e) => setSaved(e.target.checked)}
              className="size-4 accent-[var(--c-primary)]"
            />
            {tc("saved")}
          </label>
          <Button
            size="lg"
            disabled={!saved}
            onClick={() => {
              api.auth.mfa.adoptSession(done.session);
              onSignedIn();
            }}
          >
            {tc("continue")}
          </Button>
        </div>
      </AuthCard>
    );
  }

  const onSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!setup) return;
    void form
      .submit(
        (v) => ({ code: check(v.code, rules.required()) }),
        async (v) => {
          try {
            setDone(await api.auth.mfa.enrollComplete(setup.pendingToken, compact(v.code)));
          } catch (e) {
            form.setField("code", "");
            throw e;
          }
        },
      );
  };

  return (
    <AuthCard title={t("title")} description={t("subtitle")} wide>
      {startError ? (
        <div className="flex flex-col gap-4">
          <Alert tone="danger">{startError}</Alert>
          <Button variant="outline" onClick={onBack}>
            {t("restart")}
          </Button>
        </div>
      ) : !setup ? (
        <div className="flex items-center gap-2 text-sm text-fg-muted" role="status">
          <Spinner /> {t("preparing")}
        </div>
      ) : (
        <form onSubmit={onSubmit} noValidate className="flex flex-col gap-5">
          {form.formError ? <Alert tone="danger">{form.formError.message}</Alert> : null}
          <section className="flex flex-col gap-2">
            <h2 className="text-sm font-semibold">{t("step1")}</h2>
            <p className="text-sm text-fg-muted">{t("step1Hint")}</p>
            <div className="flex flex-wrap items-center gap-2 rounded-card border border-border bg-surface-muted p-3">
              <span className="text-xs text-fg-muted">{t("secretLabel")}</span>
              <code dir="ltr" className="font-mono text-base tracking-widest break-all" data-testid="mfa-secret">
                {setup.secret.match(/.{1,4}/g)?.join(" ")}
              </code>
            </div>
            <div className="flex flex-wrap gap-2">
              <Button
                variant="outline"
                size="sm"
                icon={<Copy className="size-4" aria-hidden="true" />}
                onClick={() => void navigator.clipboard?.writeText(setup.secret).catch(() => undefined)}
              >
                {t("copySecret")}
              </Button>
              <a
                href={setup.setupUri}
                className="inline-flex h-8 items-center gap-2 rounded-control border border-border-strong bg-surface px-3 text-sm font-medium text-fg hover:bg-surface-muted"
              >
                <ExternalLink className="size-4" aria-hidden="true" />
                {t("openLink")}
              </a>
            </div>
          </section>
          <section className="flex flex-col gap-2">
            <h2 className="text-sm font-semibold">{t("step2")}</h2>
            <Input
              label={t("code")}
              dir="ltr"
              autoFocus
              required
              autoComplete="one-time-code"
              inputMode="numeric"
              spellCheck={false}
              {...form.bind("code")}
            />
          </section>
          <Button type="submit" size="lg" loading={form.submitting}>
            {t("submit")}
          </Button>
          <button type="button" className="self-start text-sm text-fg-muted hover:underline" onClick={onBack}>
            {t("restart")}
          </button>
        </form>
      )}
    </AuthCard>
  );
}
