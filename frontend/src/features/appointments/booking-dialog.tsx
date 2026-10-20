"use client";

import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input, Select, Textarea } from "@/components/ui/field";
import { useClinic } from "@/features/clinic/clinic-context";
import { PatientPicker, type PickedPatient } from "@/features/patients/patient-picker";
import { isApiError, type ApiError } from "@/lib/api/client";
import type { Appointment, AppointmentSource, AvailableSlot, ClinicService, Doctor } from "@/lib/api/types";
import { useApi } from "@/lib/auth/session";
import { formatHijriDate, isIsoDate } from "@/lib/datetime";
import { mapServerErrors } from "@/lib/hooks/use-form";
import { useQuery } from "@/lib/hooks/use-query";
import { SlotPicker } from "./slot-picker";

/**
 * Book a regular appointment from the doctor's free slots, or register a walk-in (queue token, no slot).
 * The slot list comes from GET /doctors/{id}/availability, computed by the server in clinic time.
 */
export function BookingDialog({
  open,
  mode,
  initialDate,
  initialPatient,
  initialDoctorId,
  doctors,
  services,
  onClose,
  onBooked,
}: {
  open: boolean;
  mode: "booking" | "walk-in";
  initialDate: string;
  /** Prefills (e.g. booking a follow-up); the user can still change them. */
  initialPatient?: PickedPatient | null;
  initialDoctorId?: string;
  doctors: Doctor[];
  services: ClinicService[];
  onClose: () => void;
  onBooked: (appointment: Appointment) => void;
}) {
  const t = useTranslations("appointments.booking");
  const locale = useLocale();
  const api = useApi();
  const { allowedBranches, branchName } = useClinic();

  const [patient, setPatient] = useState<PickedPatient | null>(initialPatient ?? null);
  const [doctorId, setDoctorId] = useState(initialDoctorId ?? "");
  const [serviceId, setServiceId] = useState("");
  const [branchId, setBranchId] = useState("");
  const [date, setDate] = useState(initialDate);
  const [slot, setSlot] = useState<AvailableSlot | null>(null);
  const [source, setSource] = useState<AppointmentSource>("Reception");
  const [reason, setReason] = useState("");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<ApiError | null>(null);
  const [saving, setSaving] = useState(false);

  const isWalkIn = mode === "walk-in";
  const doctor = doctors.find((d) => d.id === doctorId);
  const doctorBranchIds = [...new Set(doctor?.schedule.map((w) => w.branchId) ?? [])];
  const branchOptions = allowedBranches.filter((b) => b.isActive && (doctorBranchIds.length === 0 || doctorBranchIds.includes(b.id)));

  const slotsKey = !isWalkIn && doctorId && isIsoDate(date) ? `slots:${doctorId}:${date}:${serviceId}:${branchId}` : null;
  const slots = useQuery(slotsKey, (signal) =>
    api.doctors.availability(doctorId, { date, serviceId: serviceId || undefined, branchId: branchId || undefined }, signal),
  );

  const resetSlot = () => setSlot(null);

  const submit = async () => {
    const found: Record<string, string> = {};
    if (!patient) found.patientId = t("errors.patient");
    if (!doctorId) found.doctorId = t("errors.doctor");
    if (!isWalkIn && !slot) found.startUtc = t("errors.slot");
    setErrors(found);
    if (Object.keys(found).length > 0) return;

    setSaving(true);
    setFormError(null);
    try {
      const created = await api.appointments.create({
        patientId: patient!.id,
        doctorId,
        serviceId: serviceId || null,
        branchId: isWalkIn ? branchId || (branchOptions.length === 1 ? branchOptions[0].id : null) : slot!.branchId,
        startUtc: isWalkIn ? null : slot!.startUtc,
        source: isWalkIn ? "WalkIn" : source,
        reason: reason.trim() || null,
      });
      onBooked(created);
    } catch (e) {
      if (!isApiError(e)) throw e;
      setFormError(e);
      if (e.errors) setErrors(mapServerErrors(e.errors));
      // The slot was taken meanwhile: refresh the free slots so the user can pick another.
      if (e.code === "SLOT_TAKEN" || e.code === "SLOT_IN_PAST") {
        setSlot(null);
        slots.reload();
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog
      open={open}
      onClose={onClose}
      size="lg"
      dismissible={!saving}
      title={isWalkIn ? t("walkInTitle") : t("title")}
      description={isWalkIn ? t("walkInDescription") : t("description")}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={saving}>
            {t("cancel")}
          </Button>
          <Button onClick={() => void submit()} loading={saving}>
            {isWalkIn ? t("submitWalkIn") : t("submit")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-5">
        {formError && !formError.isUpgradeRequired ? <Alert tone="danger">{formError.message}</Alert> : null}

        <PatientPicker value={patient} onChange={setPatient} error={errors.patientId} />

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Select
            label={t("doctor")}
            required
            value={doctorId}
            onChange={(e) => {
              setDoctorId(e.target.value);
              setBranchId("");
              resetSlot();
            }}
            placeholder={t("chooseDoctor")}
            options={doctors.map((d) => ({ value: d.id, label: `${d.displayNameAr} — ${d.specialty}` }))}
            error={errors.doctorId}
          />
          <Select
            label={t("service")}
            value={serviceId}
            onChange={(e) => {
              setServiceId(e.target.value);
              resetSlot();
            }}
            placeholder={t("noService")}
            options={services.map((s) => ({ value: s.id, label: `${s.nameAr} (${t("minutes", { count: s.durationMinutes })})` }))}
            error={errors.serviceId}
          />
          {branchOptions.length > 1 ? (
            <Select
              label={t("branch")}
              value={branchId}
              onChange={(e) => {
                setBranchId(e.target.value);
                resetSlot();
              }}
              placeholder={isWalkIn ? t("chooseBranch") : t("anyBranch")}
              options={branchOptions.map((b) => ({ value: b.id, label: b.name }))}
              error={errors.branchId}
            />
          ) : null}
          {!isWalkIn ? (
            <Select
              label={t("source")}
              value={source}
              onChange={(e) => setSource(e.target.value as AppointmentSource)}
              options={(["Reception", "Phone"] as const).map((s) => ({ value: s, label: t(`sources.${s}`) }))}
            />
          ) : null}
        </div>

        {!isWalkIn ? (
          <div className="flex flex-col gap-3">
            <Input
              label={t("date")}
              type="date"
              value={date}
              onChange={(e) => {
                setDate(e.target.value);
                resetSlot();
              }}
              hint={isIsoDate(date) ? formatHijriDate(date, locale) : undefined}
              className="sm:max-w-xs"
            />
            <SlotPicker
              slots={slots}
              selected={slot}
              onSelect={setSlot}
              error={errors.startUtc}
              placeholder={!doctorId ? t("chooseDoctorFirst") : undefined}
              branchLabel={branchOptions.length > 1 && !branchId ? (s) => branchName(s.branchId) : undefined}
            />
          </div>
        ) : null}

        <Textarea
          label={t("reason")}
          value={reason}
          maxLength={500}
          onChange={(e) => setReason(e.target.value)}
          error={errors.reason}
          rows={2}
        />
      </div>
    </Dialog>
  );
}
