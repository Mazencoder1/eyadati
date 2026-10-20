"use client";

import { RefreshCw } from "lucide-react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox, Input, Textarea } from "@/components/ui/field";
import type { Doctor, SaveDoctorRequest } from "@/lib/api/types";
import { useForm } from "@/lib/hooks/use-form";
import { emptyToNull } from "@/lib/utils";
import { check, rules, toLatinDigits } from "@/lib/validation/saudi";

export type DoctorFormValues = {
  displayNameAr: string;
  displayNameEn: string;
  specialty: string;
  licenseNumber: string;
  bio: string;
  consultationFee: string;
  defaultSlotMinutes: string;
  acceptsOnlineBooking: boolean;
  isActive: boolean;
};

export function doctorFormValues(d?: Doctor, defaultSlot = 15): DoctorFormValues {
  return {
    displayNameAr: d?.displayNameAr ?? "",
    displayNameEn: d?.displayNameEn ?? "",
    specialty: d?.specialty ?? "",
    licenseNumber: d?.licenseNumber ?? "",
    bio: d?.bio ?? "",
    consultationFee: d ? String(d.consultationFee) : "0",
    defaultSlotMinutes: String(d?.defaultSlotMinutes ?? defaultSlot),
    acceptsOnlineBooking: d?.acceptsOnlineBooking ?? true,
    isActive: d?.isActive ?? true,
  };
}

export function toDoctorRequest(v: DoctorFormValues): SaveDoctorRequest {
  return {
    displayNameAr: v.displayNameAr.trim(),
    displayNameEn: emptyToNull(v.displayNameEn),
    specialty: v.specialty.trim(),
    licenseNumber: emptyToNull(v.licenseNumber),
    bio: emptyToNull(v.bio),
    consultationFee: Number(toLatinDigits(v.consultationFee || "0")),
    defaultSlotMinutes: Number(toLatinDigits(v.defaultSlotMinutes)),
    acceptsOnlineBooking: v.acceptsOnlineBooking,
  };
}

/** Mirrors SaveDoctorValidator (backend). */
export function validateDoctor(v: DoctorFormValues) {
  return {
    displayNameAr: check(v.displayNameAr, rules.required(), rules.maxLength(200)),
    displayNameEn: check(v.displayNameEn, rules.maxLength(200)),
    specialty: check(v.specialty, rules.required(), rules.maxLength(100)),
    licenseNumber: check(v.licenseNumber, rules.maxLength(50)),
    bio: check(v.bio, rules.maxLength(1000)),
    consultationFee: check(v.consultationFee, rules.range(0, 100_000)),
    defaultSlotMinutes: check(v.defaultSlotMinutes, rules.required(), rules.range(5, 240)),
  };
}

export function DoctorFields({
  form,
  showActive,
}: {
  form: ReturnType<typeof useForm<DoctorFormValues>>;
  showActive?: boolean;
}) {
  const t = useTranslations("doctors.fields");
  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <Input label={t("displayNameAr")} required {...form.bind("displayNameAr")} />
        <Input label={t("displayNameEn")} dir="ltr" {...form.bind("displayNameEn")} />
        <Input label={t("specialty")} required {...form.bind("specialty")} />
        <Input label={t("licenseNumber")} hint={t("licenseHint")} dir="ltr" {...form.bind("licenseNumber")} />
        <Input label={t("consultationFee")} type="number" min={0} step="0.01" inputMode="decimal" dir="ltr" {...form.bind("consultationFee")} />
        <Input label={t("defaultSlotMinutes")} type="number" min={5} max={240} inputMode="numeric" dir="ltr" required {...form.bind("defaultSlotMinutes")} />
      </div>
      <Textarea label={t("bio")} maxLength={1000} {...form.bind("bio")} />
      <Checkbox
        label={t("acceptsOnlineBooking")}
        checked={form.values.acceptsOnlineBooking}
        onChange={(e) => form.setField("acceptsOnlineBooking", e.target.checked)}
      />
      {showActive ? (
        <Checkbox
          label={t("isActive")}
          description={t("isActiveHint")}
          checked={form.values.isActive}
          onChange={(e) => form.setField("isActive", e.target.checked)}
        />
      ) : null}
    </div>
  );
}

export function ConflictAlert({ onReload }: { onReload: () => void }) {
  const tc = useTranslations("common");
  return (
    <Alert
      tone="warning"
      title={tc("conflictTitle")}
      action={
        <Button size="sm" variant="outline" icon={<RefreshCw className="size-4" aria-hidden="true" />} onClick={onReload}>
          {tc("reload")}
        </Button>
      }
    >
      {tc("conflictBody")}
    </Alert>
  );
}
