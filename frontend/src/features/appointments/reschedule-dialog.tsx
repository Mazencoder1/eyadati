"use client";

import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/field";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { Appointment, AvailableSlot } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useApi } from "@/lib/auth/session";
import { formatGregorianDate, formatHijriDate, formatTime, isIsoDate, isoDateInZone, todayInZone } from "@/lib/datetime";
import { useQuery } from "@/lib/hooks/use-query";
import { SlotPicker } from "./slot-picker";

/**
 * Mirror of the server rule in AppointmentService.RescheduleAsync (Scheduled/Confirmed, never a walk-in) plus the
 * endpoint's permission. Used only to decide whether to offer the action; the API validates every request again.
 */
export function canReschedule(appointment: Appointment, can: (permission: string) => boolean): boolean {
  return (
    (appointment.status === "Scheduled" || appointment.status === "Confirmed") &&
    appointment.source !== "WalkIn" &&
    can(P.Appointments.Edit)
  );
}

/** Errors that mean this appointment row is stale or gone: close and let the list reload. */
const STALE_CODES = new Set(["CONCURRENCY_CONFLICT", "APPOINTMENT_NOT_EDITABLE", "APPOINTMENT_NOT_FOUND", "NOT_FOUND"]);
/** Errors about the chosen time: keep the dialog open and refresh the free slots. */
const SLOT_CODES = new Set(["SLOT_TAKEN", "SLOT_IN_PAST", "OUTSIDE_WORKING_HOURS"]);

/**
 * Moves an appointment to another free slot of the same doctor, branch and service. Slots come from
 * GET /doctors/{id}/availability; PUT /appointments/{id}/reschedule re-checks working hours and conflicts.
 */
export function RescheduleDialog({
  appointment,
  onClose,
  onRescheduled,
  onStale,
}: {
  appointment: Appointment;
  onClose: () => void;
  onRescheduled: (updated: Appointment) => void;
  /** The row changed or disappeared on the server (conflict, status change, not found): the caller reloads. */
  onStale: (message: string) => void;
}) {
  const t = useTranslations("appointments.reschedule");
  const locale = useLocale();
  const api = useApi();
  const { timeZone, branchName } = useClinic();

  const today = todayInZone(timeZone);
  const currentDate = isoDateInZone(new Date(appointment.startUtc), timeZone);
  const [date, setDate] = useState(currentDate < today ? today : currentDate);
  const [slot, setSlot] = useState<AvailableSlot | null>(null);
  const [slotError, setSlotError] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const validDate = isIsoDate(date) && date >= today;
  const slots = useQuery(validDate ? `reschedule-slots:${appointment.id}:${date}` : null, (signal) =>
    api.doctors.availability(
      appointment.doctorId,
      { date, serviceId: appointment.serviceId ?? undefined, branchId: appointment.branchId },
      signal,
    ),
  );

  const when = (utc: string) => ({
    date: formatGregorianDate(isoDateInZone(new Date(utc), timeZone), locale),
    time: formatTime(utc, timeZone, locale),
  });
  const current = when(appointment.startUtc);
  const next = slot ? when(slot.startUtc) : null;

  const submit = async () => {
    if (!slot) {
      setSlotError(t("pickSlot"));
      return;
    }
    setSaving(true);
    setFormError(null);
    setSlotError(null);
    try {
      // Only the new start and the row version: doctor, branch, tenant and duration stay as the server has them.
      const updated = await api.appointments.reschedule(appointment.id, { startUtc: slot.startUtc, rowVersion: appointment.rowVersion });
      onRescheduled(updated);
    } catch (e) {
      if (!isApiError(e)) throw e;
      if (STALE_CODES.has(e.code) || e.status === 404) {
        onStale(e.message);
      } else if (SLOT_CODES.has(e.code)) {
        setSlot(null);
        setFormError(e.code === "SLOT_TAKEN" ? t("slotTaken") : e.message);
        slots.reload();
      } else if (!e.isUpgradeRequired) {
        setFormError(e.message);
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog
      open
      onClose={onClose}
      size="lg"
      dismissible={!saving}
      title={t("title")}
      description={t("description", { patient: appointment.patientName, doctor: appointment.doctorName })}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={saving}>
            {t("keep")}
          </Button>
          <Button onClick={() => void submit()} loading={saving}>
            {t("submit")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-5">
        <dl className="grid grid-cols-1 gap-3 text-sm sm:grid-cols-2">
          <div>
            <dt className="text-fg-muted">{t("current")}</dt>
            <dd className="mt-0.5 font-semibold text-fg">
              {current.date} · <span className="ltr-nums">{current.time}</span>
            </dd>
            {branchName(appointment.branchId) ? <dd className="text-xs text-fg-subtle">{branchName(appointment.branchId)}</dd> : null}
          </div>
          <div aria-live="polite">
            <dt className="text-fg-muted">{t("new")}</dt>
            <dd className={next ? "mt-0.5 font-semibold text-primary-strong" : "mt-0.5 text-fg-subtle"}>
              {next ? (
                <>
                  {next.date} · <span className="ltr-nums">{next.time}</span>
                </>
              ) : (
                t("notChosen")
              )}
            </dd>
          </div>
        </dl>

        {formError ? <Alert tone="danger">{formError}</Alert> : null}

        <Input
          label={t("date")}
          type="date"
          required
          min={today}
          value={date}
          onChange={(e) => {
            setDate(e.target.value);
            setSlot(null);
            setFormError(null);
          }}
          error={isIsoDate(date) && date < today ? t("pastDate") : undefined}
          hint={validDate ? `${formatHijriDate(date, locale)} — ${t("sameDoctor")}` : t("sameDoctor")}
          className="sm:max-w-xs"
        />

        <SlotPicker
          slots={slots}
          selected={slot}
          onSelect={(s) => {
            setSlot(s);
            setSlotError(null);
          }}
          error={slotError}
          placeholder={!validDate ? t("chooseDate") : undefined}
        />
      </div>
    </Dialog>
  );
}
