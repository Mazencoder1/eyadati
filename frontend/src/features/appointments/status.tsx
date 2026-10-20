"use client";

import { Ban, CalendarCheck, CalendarClock, CheckCheck, LogIn, UserX } from "lucide-react";
import { useTranslations } from "next-intl";
import type { ReactNode } from "react";
import { Badge, type Tone } from "@/components/ui/badge";
import type { TransitionOption } from "./types";
import type { AppointmentStatus } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";

const statusStyle: Record<AppointmentStatus, { tone: Tone; icon: ReactNode }> = {
  Scheduled: { tone: "info", icon: <CalendarClock className="size-3.5" /> },
  Confirmed: { tone: "primary", icon: <CalendarCheck className="size-3.5" /> },
  CheckedIn: { tone: "warning", icon: <LogIn className="size-3.5" /> },
  Completed: { tone: "success", icon: <CheckCheck className="size-3.5" /> },
  Cancelled: { tone: "neutral", icon: <Ban className="size-3.5" /> },
  NoShow: { tone: "danger", icon: <UserX className="size-3.5" /> },
};

export function AppointmentStatusBadge({ status }: { status: AppointmentStatus }) {
  const t = useTranslations("appointments.status");
  const style = statusStyle[status];
  return (
    <Badge tone={style.tone} icon={style.icon}>
      {t(status)}
    </Badge>
  );
}

/**
 * Mirror of the server's state machine (Domain/Clinical/Appointment.cs → Transitions) with the permission each
 * transition needs. Used only to decide which buttons to show; the API validates the transition again.
 */
export const transitions: Record<AppointmentStatus, TransitionOption[]> = {
  Scheduled: [
    { action: "confirm", permission: P.Appointments.Edit },
    { action: "check-in", permission: P.Appointments.CheckIn },
    { action: "no-show", permission: P.Appointments.Edit, afterStartOnly: true },
    { action: "cancel", permission: P.Appointments.Cancel },
  ],
  Confirmed: [
    { action: "check-in", permission: P.Appointments.CheckIn },
    { action: "no-show", permission: P.Appointments.Edit, afterStartOnly: true },
    { action: "cancel", permission: P.Appointments.Cancel },
  ],
  CheckedIn: [
    { action: "complete", permission: P.Appointments.CheckIn },
    { action: "cancel", permission: P.Appointments.Cancel },
  ],
  Completed: [],
  Cancelled: [],
  NoShow: [],
};

export const actionIcons: Record<TransitionOption["action"], ReactNode> = {
  confirm: <CalendarCheck className="size-4" />,
  "check-in": <LogIn className="size-4" />,
  complete: <CheckCheck className="size-4" />,
  "no-show": <UserX className="size-4" />,
  cancel: <Ban className="size-4" />,
};
