"use client";

import { Plus, Trash2 } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input, Textarea } from "@/components/ui/field";
import { isApiError, type ApiError } from "@/lib/api/client";
import type { PatientDetails, Prescription, Visit } from "@/lib/api/types";
import { useAuth } from "@/lib/auth/session";
import type { ValidationCode } from "@/lib/validation/saudi";
import { emptyLine, MAX_LINES, toCreateRequest, validateLines, type MedicationLine } from "./prescription-model";

/**
 * Issues a prescription for this visit. Only the visit and the content are sent: the server derives patient, doctor
 * and branch from the visit and refuses anyone but the visit's own doctor (D8).
 */
export function PrescriptionDialog({
  visit,
  patient,
  onClose,
  onIssued,
}: {
  visit: Visit;
  patient: PatientDetails | undefined;
  onClose: () => void;
  onIssued: (rx: Prescription) => void;
}) {
  const t = useTranslations("prescriptions");
  const tc = useTranslations("common");
  const tv = useTranslations("validation");
  const tVisits = useTranslations("visits");
  const tPatients = useTranslations("patients");
  const { api } = useAuth();
  const [lines, setLines] = useState<MedicationLine[]>([emptyLine()]);
  const [notes, setNotes] = useState("");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<ApiError | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const update = (index: number, field: keyof MedicationLine, value: string) => {
    setLines((current) => current.map((l, i) => (i === index ? { ...l, [field]: value } : l)));
    setErrors((current) => {
      const key = `items[${index}].${field}`;
      if (!(key in current)) return current;
      const next = { ...current };
      delete next[key];
      return next;
    });
  };

  const submit = async () => {
    // GD-09: an empty prescription gets its own message instead of the generic "this field is required".
    const message = (key: string, code: ValidationCode) => (key === "items" && code === "required" ? t("itemsRequired") : tv(code));
    const found = Object.fromEntries(Object.entries(validateLines(lines, notes)).map(([k, code]) => [k, message(k, code)]));
    setErrors(found);
    setFormError(null);
    if (Object.keys(found).length > 0) {
      requestAnimationFrame(() => document.querySelector<HTMLElement>('dialog [aria-invalid="true"]')?.focus());
      return;
    }
    setSubmitting(true);
    try {
      onIssued(await api.prescriptions.create(toCreateRequest(visit.id, lines, notes)));
    } catch (e) {
      if (!isApiError(e)) throw e;
      // Server keys look like "items[0].MedicationName"; the form uses camelCase field names.
      if (e.errors) {
        setErrors(
          Object.fromEntries(
            Object.entries(e.errors).map(([k, m]) => [k.replace(/\.(\w)/g, (_, c: string) => `.${c.toLowerCase()}`).replace(/^(\w)/, (c) => c.toLowerCase()), m[0]]),
          ),
        );
      }
      setFormError(e);
    } finally {
      setSubmitting(false);
    }
  };

  const err = (index: number, field: keyof MedicationLine) => errors[`items[${index}].${field}`];

  return (
    <Dialog
      open
      size="xl"
      onClose={onClose}
      title={t("new")}
      description={t("newDescription", { patient: visit.patientName, doctor: visit.doctorName })}
      dismissible={!submitting}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={submitting}>
            {tc("cancel")}
          </Button>
          <Button onClick={() => void submit()} loading={submitting}>
            {t("issue")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-5">
        {patient?.medical && (patient.medical.allergies || patient.medical.chronicConditions) ? (
          <Alert tone="warning" title={tVisits("alertsTitle")}>
            {patient.medical.allergies ? (
              <p>
                <span className="font-semibold">{tPatients("fields.allergies")}:</span> {patient.medical.allergies}
              </p>
            ) : null}
            {patient.medical.chronicConditions ? (
              <p>
                <span className="font-semibold">{tPatients("fields.chronicConditions")}:</span> {patient.medical.chronicConditions}
              </p>
            ) : null}
          </Alert>
        ) : null}
        {formError ? <Alert tone="danger">{formError.message}</Alert> : null}
        {errors.items ? <Alert tone="danger">{errors.items}</Alert> : null}

        <ol className="flex flex-col gap-4">
          {lines.map((line, i) => (
            <li key={line.key} className="rounded-card border border-border p-4">
              <fieldset>
                <legend className="mb-3 flex w-full items-center justify-between text-sm font-semibold text-fg">
                  <span>{t("line", { n: i + 1 })}</span>
                  {lines.length > 1 ? (
                    <Button
                      variant="ghost"
                      size="sm"
                      icon={<Trash2 className="size-4" aria-hidden="true" />}
                      onClick={() => setLines((current) => current.filter((_, j) => j !== i))}
                      aria-label={t("removeLine", { n: i + 1 })}
                    >
                      {tc("delete")}
                    </Button>
                  ) : null}
                </legend>
                <div className="grid grid-cols-1 gap-3 sm:grid-cols-6">
                  <Input
                    className="sm:col-span-4"
                    label={t("fields.medicationName")}
                    required
                    dir="auto"
                    maxLength={200}
                    value={line.medicationName}
                    error={err(i, "medicationName")}
                    onChange={(e) => update(i, "medicationName", e.target.value)}
                  />
                  <Input
                    className="sm:col-span-2"
                    label={t("fields.strength")}
                    placeholder="500 mg"
                    dir="auto"
                    maxLength={100}
                    value={line.strength}
                    error={err(i, "strength")}
                    onChange={(e) => update(i, "strength", e.target.value)}
                  />
                  <Input
                    className="sm:col-span-2"
                    label={t("fields.dosage")}
                    placeholder={t("placeholders.dosage")}
                    maxLength={100}
                    value={line.dosage}
                    error={err(i, "dosage")}
                    onChange={(e) => update(i, "dosage", e.target.value)}
                  />
                  <Input
                    className="sm:col-span-2"
                    label={t("fields.frequency")}
                    placeholder={t("placeholders.frequency")}
                    maxLength={100}
                    value={line.frequency}
                    error={err(i, "frequency")}
                    onChange={(e) => update(i, "frequency", e.target.value)}
                  />
                  <Input
                    label={t("fields.route")}
                    placeholder={t("placeholders.route")}
                    maxLength={50}
                    value={line.route}
                    error={err(i, "route")}
                    onChange={(e) => update(i, "route", e.target.value)}
                  />
                  <Input
                    label={t("fields.durationDays")}
                    inputMode="numeric"
                    dir="ltr"
                    value={line.durationDays}
                    error={err(i, "durationDays")}
                    onChange={(e) => update(i, "durationDays", e.target.value)}
                  />
                  <Input
                    className="sm:col-span-6"
                    label={t("fields.instructions")}
                    placeholder={t("placeholders.instructions")}
                    maxLength={500}
                    value={line.instructions}
                    error={err(i, "instructions")}
                    onChange={(e) => update(i, "instructions", e.target.value)}
                  />
                </div>
              </fieldset>
            </li>
          ))}
        </ol>

        {lines.length < MAX_LINES ? (
          <div>
            <Button
              variant="outline"
              size="sm"
              icon={<Plus className="size-4" aria-hidden="true" />}
              onClick={() => setLines((current) => [...current, emptyLine()])}
            >
              {t("addLine")}
            </Button>
          </div>
        ) : null}

        <Textarea
          label={t("fields.notes")}
          rows={2}
          maxLength={1000}
          value={notes}
          error={errors.notes}
          onChange={(e) => setNotes(e.target.value)}
        />
        <p className="text-xs text-fg-subtle">{t("immutableHint")}</p>
      </div>
    </Dialog>
  );
}
