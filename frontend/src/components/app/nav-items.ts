import { CalendarClock, CalendarDays, CreditCard, History, ListChecks, Settings, Stethoscope, UserCog, Users } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { P } from "@/lib/auth/permissions";
import { HOME_PATH } from "@/lib/routes";

export interface NavItem {
  href: string;
  /** key in messages "nav" */
  labelKey: "today" | "appointments" | "patients" | "doctors" | "staff" | "settings" | "subscription" | "auditLog" | "followUps";
  icon: LucideIcon;
  /** Shown when the user holds ANY of these. Hiding is UX only; the API enforces each call. */
  anyOf: string[];
}

export const navItems: NavItem[] = [
  { href: HOME_PATH, labelKey: "today", icon: ListChecks, anyOf: [P.Appointments.View] },
  { href: "/appointments", labelKey: "appointments", icon: CalendarDays, anyOf: [P.Appointments.View] },
  { href: "/follow-ups", labelKey: "followUps", icon: CalendarClock, anyOf: [P.Appointments.View] },
  { href: "/patients", labelKey: "patients", icon: Users, anyOf: [P.Patients.View] },
  { href: "/doctors", labelKey: "doctors", icon: Stethoscope, anyOf: [P.Doctors.View] },
  { href: "/staff", labelKey: "staff", icon: UserCog, anyOf: [P.Staff.View] },
  {
    href: "/settings",
    labelKey: "settings",
    icon: Settings,
    anyOf: [P.Settings.Manage, P.Branches.Manage],
  },
  { href: "/subscription", labelKey: "subscription", icon: CreditCard, anyOf: [P.Subscription.View] },
  { href: "/audit-log", labelKey: "auditLog", icon: History, anyOf: [P.AuditLogs.View] },
];

export function visibleNavItems(canAny: (...p: string[]) => boolean): NavItem[] {
  return navItems.filter((item) => canAny(...item.anyOf));
}
