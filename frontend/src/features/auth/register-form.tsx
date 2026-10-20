"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox, Input } from "@/components/ui/field";
import { useAuth } from "@/lib/auth/session";
import { useForm } from "@/lib/hooks/use-form";
import { HOME_PATH } from "@/lib/routes";
import { emptyToNull } from "@/lib/utils";
import { check, rules } from "@/lib/validation/saudi";
import { AuthCard } from "./auth-card";

export function RegisterForm() {
  const t = useTranslations("auth.register");
  const router = useRouter();
  const { api } = useAuth();
  const form = useForm({
    clinicName: "",
    slug: "",
    city: "",
    clinicPhone: "",
    ownerFullName: "",
    email: "",
    password: "",
    ownerMobile: "",
    acceptTerms: false as boolean,
  });

  const onSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    void form.submit(
      (v) => ({
        clinicName: check(v.clinicName, rules.required(), rules.maxLength(200)),
        slug: check(v.slug, rules.slug()),
        city: check(v.city, rules.required(), rules.maxLength(100)),
        clinicPhone: check(v.clinicPhone, rules.required(), rules.saudiMobile()),
        ownerFullName: check(v.ownerFullName, rules.required(), rules.maxLength(200)),
        email: check(v.email, rules.required(), rules.email()),
        password: check(v.password, rules.password()),
        ownerMobile: check(v.ownerMobile, rules.saudiMobile()),
        acceptTerms: v.acceptTerms ? null : "required",
      }),
      async (v) => {
        await api.auth.register({
          clinicName: v.clinicName.trim(),
          slug: emptyToNull(v.slug),
          city: v.city.trim(),
          clinicPhone: v.clinicPhone.trim(),
          ownerFullName: v.ownerFullName.trim(),
          email: v.email.trim(),
          password: v.password,
          ownerMobile: emptyToNull(v.ownerMobile),
          acceptTerms: v.acceptTerms,
        });
        router.replace(HOME_PATH);
      },
    );
  };

  return (
    <AuthCard
      wide
      title={t("title")}
      description={t("subtitle")}
      footer={
        <>
          {t("haveAccount")}{" "}
          <Link href="/login" className="font-medium text-primary hover:underline">
            {t("loginLink")}
          </Link>
        </>
      }
    >
      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-5">
        {form.formError ? <Alert tone="danger">{form.formError.message}</Alert> : null}

        <fieldset className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <legend className="mb-3 text-sm font-semibold text-fg-muted">{t("clinicSection")}</legend>
          <Input label={t("clinicName")} required autoComplete="organization" {...form.bind("clinicName")} />
          <Input label={t("city")} required autoComplete="address-level2" {...form.bind("city")} />
          <Input
            label={t("clinicPhone")}
            required
            type="tel"
            inputMode="tel"
            dir="ltr"
            placeholder="05XXXXXXXX"
            {...form.bind("clinicPhone")}
          />
          <Input label={t("slug")} hint={t("slugHint")} dir="ltr" autoCapitalize="none" {...form.bind("slug")} />
        </fieldset>

        <fieldset className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <legend className="mb-3 text-sm font-semibold text-fg-muted">{t("ownerSection")}</legend>
          <Input label={t("ownerFullName")} required autoComplete="name" {...form.bind("ownerFullName")} />
          <Input
            label={t("ownerMobile")}
            type="tel"
            inputMode="tel"
            dir="ltr"
            placeholder="05XXXXXXXX"
            autoComplete="tel"
            {...form.bind("ownerMobile")}
          />
          <Input
            label={t("email")}
            required
            type="email"
            inputMode="email"
            dir="ltr"
            autoComplete="email"
            {...form.bind("email")}
          />
          <Input
            label={t("password")}
            required
            type="password"
            autoComplete="new-password"
            hint={t("passwordHint")}
            {...form.bind("password")}
          />
        </fieldset>

        <div>
          <Checkbox
            label={t("acceptTerms")}
            checked={form.values.acceptTerms}
            onChange={(e) => form.setField("acceptTerms", e.target.checked)}
            aria-invalid={form.errors.acceptTerms ? true : undefined}
          />
          {form.errors.acceptTerms ? (
            <p className="mt-1 text-xs font-medium text-danger-strong">{form.errors.acceptTerms}</p>
          ) : null}
        </div>

        <Button type="submit" size="lg" loading={form.submitting}>
          {t("submit")}
        </Button>
      </form>
    </AuthCard>
  );
}
