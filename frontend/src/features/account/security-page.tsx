"use client";

import { ShieldCheck } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { useEffect, useRef, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/field";
import { LoadingBlock } from "@/components/ui/spinner";
import { AuthCard } from "@/features/auth/auth-card";
import { RecoveryCodes } from "@/features/auth/recovery-codes";
import { useAuth } from "@/lib/auth/session";
import { useForm } from "@/lib/hooks/use-form";
import { HOME_PATH } from "@/lib/routes";
import { check, rules } from "@/lib/validation/saudi";

const compact = (value: string) => value.replace(/\s+/g, "");

/**
 * Two-step verification for platform accounts: replace the recovery codes, or remove the authenticator. Platform
 * sessions have no clinic, so this lives outside the clinic shell. The API enforces everything; a clinic user who
 * opens the URL is simply sent home.
 */
export function SecurityPage() {
  const t = useTranslations("security");
  const router = useRouter();
  const { status, session, signedOut } = useAuth();
  const leaving = useRef(false);

  useEffect(() => {
    if (leaving.current) return;
    if (status === "anonymous") router.replace(signedOut ? "/login" : `/login?next=${encodeURIComponent("/security")}`);
  }, [status, signedOut, router]);

  const isPlatform = !!session && session.user.platformRole !== "None";
  useEffect(() => {
    if (status === "authenticated" && !isPlatform) router.replace(HOME_PATH);
  }, [status, isPlatform, router]);

  if (status !== "authenticated" || !session || !isPlatform) return <LoadingBlock label={t("title")} className="min-h-dvh" />;

  return (
    <AuthCard title={t("title")} description={t("subtitle")} wide footer={<Link href={HOME_PATH} className="font-medium text-primary hover:underline">{t("back")}</Link>}>
      <div className="flex flex-col gap-6">
        <Badge tone="success" icon={<ShieldCheck className="size-3.5" aria-hidden="true" />}>
          {t("status")}
        </Badge>
        <section className="flex flex-col gap-2">
          <h2 className="text-sm font-semibold">{t("recovery.title")}</h2>
          <p className="text-sm text-fg-muted">{t("recovery.description")}</p>
          <div>
            <RegenerateCodes />
          </div>
        </section>
        <section className="flex flex-col gap-2 border-t border-border pt-5">
          <h2 className="text-sm font-semibold">{t("remove.title")}</h2>
          <p className="text-sm text-fg-muted">{t("remove.description")}</p>
          <div>
            <RemoveMfa onRemoved={() => { leaving.current = true; router.replace("/login"); }} />
          </div>
        </section>
      </div>
    </AuthCard>
  );
}

function RegenerateCodes() {
  const t = useTranslations("security.recovery");
  const { api } = useAuth();
  const [open, setOpen] = useState(false);
  const [codes, setCodes] = useState<string[] | null>(null);
  const form = useForm({ code: "" });

  const close = () => {
    setOpen(false);
    setCodes(null); // the codes leave memory with the dialog
    form.setValues({ code: "" });
    form.setFormError(null);
  };

  return (
    <>
      <Button variant="outline" onClick={() => setOpen(true)}>
        {t("action")}
      </Button>
      <Dialog
        open={open}
        onClose={close}
        title={codes ? t("newCodesTitle") : t("dialogTitle")}
        description={codes ? undefined : t("dialogDescription")}
        footer={
          codes ? (
            <Button onClick={close}>{t("done")}</Button>
          ) : (
            <Button type="submit" form="regenerate-codes-form" loading={form.submitting}>
              {t("confirm")}
            </Button>
          )
        }
      >
        {codes ? (
          <div className="flex flex-col gap-3">
            <Alert tone="warning">{t("newCodesWarning")}</Alert>
            <RecoveryCodes codes={codes} />
          </div>
        ) : (
          <form
            id="regenerate-codes-form"
            noValidate
            className="flex flex-col gap-3"
            onSubmit={(e) => {
              e.preventDefault();
              void form.submit(
                (v) => ({ code: check(v.code, rules.required()) }),
                async (v) => {
                  setCodes((await api.auth.mfa.regenerateRecoveryCodes(compact(v.code))).recoveryCodes);
                },
              );
            }}
          >
            {form.formError ? <Alert tone="danger">{form.formError.message}</Alert> : null}
            <Input label={t("code")} dir="ltr" autoFocus required autoComplete="one-time-code" inputMode="numeric" spellCheck={false} {...form.bind("code")} />
          </form>
        )}
      </Dialog>
    </>
  );
}

function RemoveMfa({ onRemoved }: { onRemoved: () => void }) {
  const t = useTranslations("security.remove");
  const { api } = useAuth();
  const [open, setOpen] = useState(false);
  const [useRecovery, setUseRecovery] = useState(false);
  const form = useForm({ password: "", code: "" });

  const close = () => {
    setOpen(false);
    form.setValues({ password: "", code: "" });
    form.setFormError(null);
  };

  return (
    <>
      <Button variant="danger" onClick={() => setOpen(true)}>
        {t("action")}
      </Button>
      <Dialog
        open={open}
        onClose={close}
        title={t("dialogTitle")}
        description={t("dialogDescription")}
        dismissible={!form.submitting}
        footer={
          <Button variant="danger" type="submit" form="remove-mfa-form" loading={form.submitting}>
            {t("confirm")}
          </Button>
        }
      >
        <form
          id="remove-mfa-form"
          noValidate
          className="flex flex-col gap-3"
          onSubmit={(e) => {
            e.preventDefault();
            void form.submit(
              (v) => ({ password: check(v.password, rules.required()), code: check(v.code, rules.required()) }),
              async (v) => {
                try {
                  await api.auth.mfa.remove(v.password, useRecovery ? { recoveryCode: v.code.trim() } : { totpCode: compact(v.code) });
                  onRemoved();
                } catch (e) {
                  form.setField("code", "");
                  throw e;
                }
              },
            );
          }}
        >
          {form.formError ? <Alert tone="danger">{form.formError.message}</Alert> : null}
          <Input label={t("password")} type="password" autoComplete="current-password" autoFocus required {...form.bind("password")} />
          <Input
            key={useRecovery ? "recovery" : "totp"}
            label={useRecovery ? t("recoveryCode") : t("code")}
            dir="ltr"
            required
            autoComplete="one-time-code"
            inputMode={useRecovery ? "text" : "numeric"}
            spellCheck={false}
            {...form.bind("code")}
          />
          <button
            type="button"
            className="self-start text-sm font-medium text-primary hover:underline"
            onClick={() => {
              setUseRecovery((v) => !v);
              form.setField("code", "");
            }}
          >
            {useRecovery ? t("useApp") : t("useRecovery")}
          </button>
        </form>
      </Dialog>
    </>
  );
}
