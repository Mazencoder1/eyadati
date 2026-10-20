"use client";

import { CalendarSync } from "lucide-react";
import { useLocale, useTranslations } from "next-intl";
import { useState, type ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog, Dialog } from "@/components/ui/dialog";
import { Textarea } from "@/components/ui/field";
import { ActionMenu } from "@/components/ui/menu";
import { useToast } from "@/components/ui/toast";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { AppointmentAction } from "@/lib/api/endpoints";
import type { Appointment } from "@/lib/api/types";
import { useAuth } from "@/lib/auth/session";
import { formatGregorianDate, formatTime, isoDateInZone } from "@/lib/datetime";
import { canReschedule, RescheduleDialog } from "./reschedule-dialog";
import { actionIcons, transitions } from "./status";
import type { TransitionOption } from "./types";

/** Transitions the current user may offer for this appointment now (UI gating only; the API re-validates). */
export function availableTransitions(
  appointment: Appointment,
  can: (permission: string) => boolean,
  now: number,
): TransitionOption[] {
  return transitions[appointment.status].filter(
    (o) => can(o.permission) && (!o.afterStartOnly || now >= new Date(appointment.startUtc).getTime()),
  );
}

/**
 * Status changes for appointment lists (board, today's queue): runs the API call, reports the outcome, and owns the
 * confirmation / cancel-with-reason / reschedule dialogs. `onUpdated` receives the server's updated row; `onConflict`
 * is called whenever the list must be reloaded (a concurrency conflict, a stale row, or a move to another day).
 */
export function useAppointmentActions({
  onUpdated,
  onConflict,
}: {
  onUpdated: (updated: Appointment) => void;
  onConflict: () => void;
}) {
  const t = useTranslations("appointments");
  const locale = useLocale();
  const toast = useToast();
  const { api } = useAuth();
  const { timeZone } = useClinic();
  const [pending, setPending] = useState<{ appointment: Appointment; action: AppointmentAction } | null>(null);
  const [rescheduling, setRescheduling] = useState<Appointment | null>(null);
  const [cancelReason, setCancelReason] = useState("");
  const [cancelError, setCancelError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const run = async (appointment: Appointment, action: AppointmentAction, reason?: string) => {
    setBusyId(appointment.id);
    try {
      const updated = await api.appointments.changeStatus(appointment.id, action, appointment.rowVersion, reason);
      onUpdated(updated);
      toast({ tone: "success", title: t(`actions.done.${action}`) });
      return true;
    } catch (e) {
      if (!isApiError(e)) throw e;
      if (e.isConcurrencyConflict) onConflict();
      if (action === "cancel") setCancelError(e.message);
      else toast({ tone: "danger", title: e.message });
      return false;
    } finally {
      setBusyId(null);
    }
  };

  /** Opens the confirmation (or cancel-reason) dialog for an action. */
  const request = (appointment: Appointment, action: AppointmentAction) => {
    setCancelReason("");
    setCancelError(null);
    setPending({ appointment, action });
  };

  const dialogs: ReactNode =
    rescheduling ? (
      <RescheduleDialog
        appointment={rescheduling}
        onClose={() => setRescheduling(null)}
        onRescheduled={(updated) => {
          setRescheduling(null);
          onUpdated(updated);
          // Lists are filtered by day: reload so a move to another day leaves this list.
          onConflict();
          toast({
            tone: "success",
            title: t("reschedule.done", {
              date: formatGregorianDate(isoDateInZone(new Date(updated.startUtc), timeZone), locale),
              time: formatTime(updated.startUtc, timeZone, locale),
            }),
          });
        }}
        onStale={(message) => {
          setRescheduling(null);
          onConflict();
          toast({ tone: "danger", title: message });
        }}
      />
    ) : pending?.action === "cancel" ? (
      <Dialog
        open
        onClose={() => setPending(null)}
        title={t("cancelDialog.title")}
        description={t("cancelDialog.description", { patient: pending.appointment.patientName })}
        size="sm"
        dismissible={busyId === null}
        footer={
          <>
            <Button variant="outline" onClick={() => setPending(null)} disabled={busyId !== null}>
              {t("cancelDialog.keep")}
            </Button>
            <Button
              variant="danger"
              loading={busyId === pending.appointment.id}
              onClick={async () => {
                if (!cancelReason.trim()) {
                  setCancelError(t("cancelDialog.reasonRequired"));
                  return;
                }
                if (await run(pending.appointment, "cancel", cancelReason.trim())) setPending(null);
              }}
            >
              {t("cancelDialog.confirm")}
            </Button>
          </>
        }
      >
        <Textarea
          label={t("cancelDialog.reason")}
          required
          value={cancelReason}
          maxLength={500}
          onChange={(e) => setCancelReason(e.target.value)}
          error={cancelError}
        />
      </Dialog>
    ) : pending ? (
      <ConfirmDialog
        open
        onClose={() => setPending(null)}
        title={t(`actions.${pending.action}`)}
        description={t("confirmAction", { patient: pending.appointment.patientName })}
        confirmLabel={t(`actions.${pending.action}`)}
        tone={pending.action === "no-show" ? "danger" : "primary"}
        loading={busyId === pending.appointment.id}
        onConfirm={async () => {
          await run(pending.appointment, pending.action);
          setPending(null);
        }}
      />
    ) : null;

  return { run, request, reschedule: setRescheduling, busyId, dialogs };
}

export type AppointmentActions = ReturnType<typeof useAppointmentActions>;

/**
 * Inline actions for one appointment: the main forward step as a button (no confirmation), everything else —
 * including the destructive cancel / no-show — in a menu that asks for confirmation first.
 */
export function AppointmentRowActions({
  appointment,
  actions,
  options,
}: {
  appointment: Appointment;
  actions: AppointmentActions;
  options: TransitionOption[];
}) {
  const t = useTranslations("appointments");
  const { can } = useAuth();
  const movable = canReschedule(appointment, can);
  if (options.length === 0 && !movable) return null;
  const forward = options.filter((o) => o.action !== "cancel" && o.action !== "no-show");
  // A walk-in is already at the desk: checking in is the next step, confirming is secondary.
  if (appointment.source === "WalkIn") forward.sort((a, b) => Number(b.action === "check-in") - Number(a.action === "check-in"));
  const [primary, ...rest] = forward;
  const destructive = options.filter((o) => o.action === "cancel" || o.action === "no-show");
  const menuAction = (o: TransitionOption) => ({
    id: o.action,
    label: t(`actions.${o.action}`),
    icon: actionIcons[o.action],
    tone: o.action === "cancel" || o.action === "no-show" ? ("danger" as const) : ("default" as const),
    onSelect: () => actions.request(appointment, o.action),
  });
  return (
    <>
      {primary ? (
        <Button
          size="sm"
          variant="secondary"
          loading={actions.busyId === appointment.id}
          icon={<span aria-hidden="true">{actionIcons[primary.action]}</span>}
          onClick={() => void actions.run(appointment, primary.action)}
        >
          {t(`actions.${primary.action}`)}
        </Button>
      ) : null}
      <ActionMenu
        label={t("actions.more", { patient: appointment.patientName })}
        actions={[
          ...rest.map(menuAction),
          ...(movable
            ? [
                {
                  id: "reschedule",
                  label: t("actions.reschedule"),
                  icon: <CalendarSync className="size-4" />,
                  onSelect: () => actions.reschedule(appointment),
                },
              ]
            : []),
          ...destructive.map(menuAction),
        ]}
      />
    </>
  );
}
