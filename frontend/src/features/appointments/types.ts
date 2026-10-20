import type { AppointmentAction } from "@/lib/api/endpoints";

export interface TransitionOption {
  action: AppointmentAction;
  permission: string;
  /** NO_SHOW_BEFORE_START: only offered once the appointment time has passed. */
  afterStartOnly?: boolean;
}
