import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { FollowUpItem } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { addDays, todayInZone } from "@/lib/datetime";
import { FollowUpsPage } from "./follow-ups-page";

const ZONE = "Asia/Riyadh";
const today = todayInZone(ZONE);

const rows: FollowUpItem[] = [
  { patientId: "p1", patientName: "سارة أحمد", patientMrn: "P-000001", followUpDate: addDays(today, -2), doctorId: "d1", doctorName: "د. خالد", branchId: "b1", booked: false },
  { patientId: "p2", patientName: "منى علي", patientMrn: "P-000002", followUpDate: addDays(today, 3), doctorId: "d1", doctorName: "د. خالد", branchId: "b1", booked: true, nextAppointmentUtc: `${addDays(today, 3)}T07:00:00Z` },
];

const api = {
  followUps: { list: vi.fn() },
  doctors: { list: vi.fn(), availability: vi.fn() },
  services: { list: vi.fn() },
  patients: { list: vi.fn() },
  appointments: { create: vi.fn() },
};
let permissions: string[] = [];
const can = (p: string) => permissions.includes(p);
const canAny = (...p: string[]) => p.some(can);
const toast = vi.fn();

vi.mock("@/lib/auth/session", () => ({ useAuth: () => ({ api, can, canAny }), useApi: () => api }));
vi.mock("@/features/clinic/clinic-context", () => ({
  useClinic: () => ({ timeZone: ZONE, allowedBranches: [{ id: "b1", name: "الرئيسي", isActive: true }], branchName: () => "الرئيسي" }),
}));
vi.mock("@/components/ui/toast", () => ({ useToast: () => toast }));

beforeAll(() => {
  HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) {
    this.setAttribute("open", "");
  };
  HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) {
    this.removeAttribute("open");
  };
});

beforeEach(() => {
  permissions = [P.Appointments.View, P.Appointments.Create, P.Patients.View];
  api.followUps.list.mockReset().mockResolvedValue({ items: rows, meta: { page: 1, pageSize: 25, totalCount: 2, totalPages: 1 } });
  api.doctors.list.mockReset().mockResolvedValue({
    items: [{ id: "d1", displayNameAr: "د. خالد", specialty: "طب عام", schedule: [{ branchId: "b1" }] }],
  });
  api.doctors.availability.mockReset().mockResolvedValue([]);
  api.services.list.mockReset().mockResolvedValue([]);
  toast.mockReset();
});
afterEach(cleanup);

function setup(locale: "ar" | "en" = "ar") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone={ZONE}>
      <FollowUpsPage />
    </NextIntlClientProvider>,
  );
}

const f = ar.followUps;
const lastQuery = () => api.followUps.list.mock.calls.at(-1)![0];

describe("Follow-ups page", () => {
  it("lists follow-ups with booking status and flags overdue ones; defaults to the next 7 days, not booked", async () => {
    setup();
    const table = await screen.findByRole("table", { name: f.title });
    expect(within(table).getByText("سارة أحمد")).toBeTruthy();
    expect(within(table).getByText(f.overdue)).toBeTruthy();
    expect(within(table).getByText(f.notBooked)).toBeTruthy();
    expect(within(table).getByText(f.bookedFor, { exact: false })).toBeTruthy();
    expect(lastQuery()).toMatchObject({ from: today, to: addDays(today, 7), booked: false, doctorId: undefined });
    // Only a not-yet-booked follow-up offers booking.
    expect(within(table).getAllByRole("button", { name: /حجز موعد متابعة/ })).toHaveLength(1);
  });

  it("filters by status, doctor and dates; an inverted range is flagged and not queried", async () => {
    setup();
    await screen.findByRole("table");
    await act(async () => fireEvent.change(screen.getByLabelText(f.filters.status), { target: { value: "" } }));
    expect(lastQuery()).toMatchObject({ booked: undefined });
    await act(async () => fireEvent.change(screen.getByLabelText(f.filters.doctor), { target: { value: "d1" } }));
    expect(lastQuery()).toMatchObject({ doctorId: "d1" });
    const calls = api.followUps.list.mock.calls.length;
    await act(async () => fireEvent.change(screen.getByLabelText(f.filters.to), { target: { value: addDays(today, -5) } }));
    expect(screen.getByLabelText(f.filters.to).getAttribute("aria-invalid")).toBe("true");
    expect(api.followUps.list.mock.calls.length).toBe(calls);
  });

  it("opens the booking dialog prefilled with the patient and the doctor", async () => {
    setup();
    fireEvent.click(await screen.findByRole("button", { name: "حجز موعد متابعة لـ سارة أحمد" }));
    const dialog = await screen.findByRole("dialog", { name: ar.appointments.booking.title });
    expect(within(dialog).getByText("سارة أحمد")).toBeTruthy();
    expect((within(dialog).getByLabelText(new RegExp(ar.appointments.booking.doctor)) as HTMLSelectElement).value).toBe("d1");
  });

  it("is read-only without Appointments.Create and shows names without links without Patients.View", async () => {
    permissions = [P.Appointments.View];
    setup();
    const table = await screen.findByRole("table");
    expect(within(table).queryByRole("button", { name: /حجز موعد متابعة/ })).toBeNull();
    expect(within(table).queryByRole("link", { name: "سارة أحمد" })).toBeNull();
    expect(api.services.list).not.toHaveBeenCalled();
  });

  it("is a no-permission screen without Appointments.View", () => {
    permissions = [P.Patients.View];
    setup();
    expect(screen.getByText(ar.shell.forbiddenTitle)).toBeTruthy();
    expect(api.followUps.list).not.toHaveBeenCalled();
  });

  it("renders in English", async () => {
    setup("en");
    const table = await screen.findByRole("table", { name: "Follow-ups" });
    expect(within(table).getByText("Overdue")).toBeTruthy();
    expect(within(table).getByRole("button", { name: "Book a follow-up for سارة أحمد" })).toBeTruthy();
  });
});
