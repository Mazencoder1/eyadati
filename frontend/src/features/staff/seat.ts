/**
 * How a staff seat is shown. A pending invitation (status "Invited", T-010) carries masked fields — fullName = email,
 * userId = empty GUID, no phone / last login — whether or not the email already has an account, so the UI shows the
 * invited email only and never a name.
 */
import type { StaffMember } from "@/lib/api/types";

export const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";

export interface SeatView {
  pending: boolean;
  /** Main line: the person's name, or the invited email for a pending seat. */
  title: string;
  /** Second line (email) — omitted when it would repeat the title. */
  subtitle: string | null;
  /** Menu entries the seat supports (permission and owner/self checks are applied by the caller). */
  canResend: boolean;
  /** "disable" on a pending seat revokes the invitation. */
  disableKind: "revoke" | "disable" | null;
  /** "enable" on a disabled seat sends a new invitation (the person must accept again). */
  canReinvite: boolean;
}

export function seatView(s: StaffMember): SeatView {
  const pending = s.status === "Invited";
  return {
    pending,
    title: pending ? s.email : s.fullName,
    subtitle: pending ? null : s.email,
    canResend: pending,
    disableKind: s.status === "Disabled" ? null : pending ? "revoke" : "disable",
    canReinvite: s.status === "Disabled",
  };
}

/** Is this seat the signed-in user? Pending seats never are (their userId is masked). */
export function isSelf(s: StaffMember, userId: string | undefined): boolean {
  return !!userId && s.userId !== EMPTY_GUID && s.userId === userId;
}

/** Label for picking a member (e.g. when creating a doctor profile). */
export function seatLabel(s: StaffMember, pendingSuffix: string): string {
  return s.status === "Invited" ? `${s.email} — ${pendingSuffix}` : `${s.fullName} — ${s.email}`;
}
