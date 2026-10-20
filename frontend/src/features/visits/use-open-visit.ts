"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { useToast } from "@/components/ui/toast";
import { isApiError } from "@/lib/api/client";
import type { Appointment, Guid } from "@/lib/api/types";
import { useAuth } from "@/lib/auth/session";

/**
 * Opens the visit of a checked-in appointment, recording it first if none exists yet. One visit per appointment is
 * enforced by the API (VISIT_ALREADY_RECORDED), so the lookup-then-create race simply falls back to the lookup.
 * Patient, doctor and branch come from the appointment; the API checks the caller may record for that doctor.
 */
export function useOpenVisit() {
  const router = useRouter();
  const toast = useToast();
  const { api } = useAuth();
  const [busyId, setBusyId] = useState<Guid | null>(null);

  const find = async (appointmentId: Guid) => (await api.visits.list({ appointmentId, pageSize: 1 })).items[0]?.id;

  const open = async (appointment: Appointment) => {
    setBusyId(appointment.id);
    try {
      let visitId = await find(appointment.id);
      if (!visitId) {
        try {
          visitId = (
            await api.visits.create({ patientId: appointment.patientId, doctorId: appointment.doctorId, appointmentId: appointment.id })
          ).id;
        } catch (e) {
          if (isApiError(e) && e.code === "VISIT_ALREADY_RECORDED") visitId = await find(appointment.id);
          else throw e;
        }
      }
      if (visitId) router.push(`/visits/${visitId}`);
    } catch (e) {
      toast({ tone: "danger", title: isApiError(e) ? e.message : String(e) });
    } finally {
      setBusyId(null);
    }
  };

  return { open, busyId };
}
