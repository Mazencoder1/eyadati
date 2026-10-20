import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { ApiError } from "@/lib/api/client";
import type { Appointment, AvailableSlot } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { addDays, todayInZone } from "@/lib/datetime";
import { AppointmentRowActions, availableTransitions, useAppointmentActions } from "./appointment-actions";
import { canReschedule } from "./reschedule-dialog";

const ZONE = "Asia/Riyadh";

// ---- test doubles for the app context (session, clinic, toasts); the components themselves are real
const api = {
  doctors: { availability: vi.fn() },
  appointments: { reschedule: vi.fn(), changeStatus: vi.fn() },
};
let permissions: string[] = [];
const can = (p: string) => permissions.includes(p);
const toast = vi.fn();

vi.mock("@/lib/auth/session", () => ({
  useAuth: () => ({ api, can }),
  useApi: () => api,
}));
vi.mock("@/features/clinic/clinic-context", () => ({
  useClinic: () => ({ timeZone: ZONE, branchName: () => "الفرع الرئيسي" }),
}));
vi.mock("@/components/ui/toast", () => ({ useToast: () => toast }));

beforeAll(() => {
  // jsdom has no modal dialog support; the real browser behaviour (focus trap, Esc) is verified in the browser.
  HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) {
    this.setAttribute("open", "");
  };
  HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) {
    this.removeAttribute("open");
  };
});

const tomorrow = addDays(todayInZone(ZONE), 1);
const NOW = Date.now();

function appointment(overrides: Partial<Appointment> = {}): Appointment {
  return {
    id: "appt-1",
    branchId: "branch-1",
    patientId: "patient-1",
    patientName: "سارة أحمد",
    patientMrn: "MRN-1",
    patientMobile: "+966500000000",
    doctorId: "doctor-1",
    doctorName: "د. خالد",
    serviceId: null,
    startUtc: `${tomorrow}T07:00:00Z`, // 10:00 Riyadh
    endUtc: `${tomorrow}T07:15:00Z`,
    status: "Scheduled",
    source: "Phone",
    rowVersion: "rv-1",
    ...overrides,
  };
}

const slots: AvailableSlot[] = [
  { startUtc: `${tomorrow}T08:00:00Z`, endUtc: `${tomorrow}T08:15:00Z`, branchId: "branch-1", localTime: "11:00" },
  { startUtc: `${tomorrow}T09:00:00Z`, endUtc: `${tomorrow}T09:15:00Z`, branchId: "branch-1", localTime: "12:00" },
];

function Harness({ appt, onUpdated, onConflict }: { appt: Appointment; onUpdated: () => void; onConflict: () => void }) {
  const actions = useAppointmentActions({ onUpdated, onConflict });
  return (
    <>
      <AppointmentRowActions appointment={appt} actions={actions} options={availableTransitions(appt, can, NOW)} />
      {actions.dialogs}
    </>
  );
}

function setup(appt = appointment(), locale: "ar" | "en" = "ar") {
  const onUpdated = vi.fn();
  const onConflict = vi.fn();
  render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone={ZONE}>
      <div dir={locale === "ar" ? "rtl" : "ltr"}>
        <Harness appt={appt} onUpdated={onUpdated} onConflict={onConflict} />
      </div>
    </NextIntlClientProvider>,
  );
  return { onUpdated, onConflict };
}

const m = ar.appointments;

function openMenu(patient = "سارة أحمد", messages = ar) {
  fireEvent.click(screen.getByRole("button", { name: messages.appointments.actions.more.replace("{patient}", patient) }));
}

async function openReschedule(messages = ar) {
  openMenu("سارة أحمد", messages);
  fireEvent.click(screen.getByRole("menuitem", { name: messages.appointments.actions.reschedule }));
  const dialog = await screen.findByRole("dialog", { name: messages.appointments.reschedule.title });
  await within(dialog).findByText("11:00");
  return dialog;
}

beforeEach(() => {
  permissions = [P.Appointments.View, P.Appointments.Edit, P.Appointments.Cancel, P.Appointments.CheckIn];
  api.doctors.availability.mockReset().mockResolvedValue(slots);
  api.appointments.reschedule.mockReset();
  toast.mockReset();
});
afterEach(cleanup);

describe("canReschedule (mirror of the server rule, UI gating only)", () => {
  it("offers it for scheduled and confirmed appointments to holders of Appointments.Edit", () => {
    expect(canReschedule(appointment(), can)).toBe(true);
    expect(canReschedule(appointment({ status: "Confirmed" }), can)).toBe(true);
  });

  it("never for walk-ins, closed appointments, or without Appointments.Edit", () => {
    expect(canReschedule(appointment({ source: "WalkIn" }), can)).toBe(false);
    for (const status of ["CheckedIn", "Completed", "Cancelled", "NoShow"] as const) {
      expect(canReschedule(appointment({ status }), can)).toBe(false);
    }
    permissions = [P.Appointments.View, P.Appointments.Cancel];
    expect(canReschedule(appointment(), can)).toBe(false);
  });
});

describe("Reschedule action in the appointment menu", () => {
  it("appears for an eligible appointment, before the destructive actions", () => {
    setup();
    openMenu();
    const items = screen.getAllByRole("menuitem").map((i) => i.textContent);
    expect(items).toContain(m.actions.reschedule);
    expect(items.indexOf(m.actions.reschedule)).toBeLessThan(items.indexOf(m.actions.cancel));
  });

  it("does not appear for a walk-in", () => {
    setup(appointment({ source: "WalkIn", tokenNumber: "T-01" }));
    openMenu();
    expect(screen.queryByRole("menuitem", { name: m.actions.reschedule })).toBeNull();
  });

  it("does not appear without Appointments.Edit", () => {
    permissions = [P.Appointments.View, P.Appointments.Cancel];
    setup();
    openMenu();
    expect(screen.queryByRole("menuitem", { name: m.actions.reschedule })).toBeNull();
  });
});

describe("Reschedule dialog", () => {
  it("opens with the current time and loads the server's free slots for the same doctor and branch", async () => {
    setup();
    const dialog = await openReschedule();
    expect(within(dialog).getByText(m.reschedule.current)).toBeTruthy();
    expect(within(dialog).getByText("10:00")).toBeTruthy();
    expect(within(dialog).getByText(m.reschedule.notChosen)).toBeTruthy();
    expect(api.doctors.availability).toHaveBeenCalledWith(
      "doctor-1",
      { date: tomorrow, serviceId: undefined, branchId: "branch-1" },
      expect.anything(),
    );
    expect(within(dialog).getAllByRole("radio")).toHaveLength(2);
  });

  it("shows the chosen slot as the new time", async () => {
    setup();
    const dialog = await openReschedule();
    fireEvent.click(within(dialog).getByLabelText("12:00"));
    const newTime = within(dialog).getByText(m.reschedule.new).parentElement!;
    expect(newTime.textContent).toContain("12:00");
  });

  it("asks for a slot before submitting", async () => {
    setup();
    const dialog = await openReschedule();
    fireEvent.click(within(dialog).getByRole("button", { name: m.reschedule.submit }));
    expect(await within(dialog).findByText(m.reschedule.pickSlot)).toBeTruthy();
    expect(api.appointments.reschedule).not.toHaveBeenCalled();
  });

  it("sends only the new start and row version, then refreshes the list and reports the new time", async () => {
    const updated = appointment({ startUtc: slots[0].startUtc, endUtc: slots[0].endUtc, rowVersion: "rv-2" });
    api.appointments.reschedule.mockResolvedValue(updated);
    const { onUpdated, onConflict } = setup();
    const dialog = await openReschedule();
    fireEvent.click(within(dialog).getByLabelText("11:00"));
    await act(async () => fireEvent.click(within(dialog).getByRole("button", { name: m.reschedule.submit })));

    expect(api.appointments.reschedule).toHaveBeenCalledWith("appt-1", { startUtc: slots[0].startUtc, rowVersion: "rv-1" });
    expect(onUpdated).toHaveBeenCalledWith(updated);
    expect(onConflict).toHaveBeenCalled();
    expect(toast).toHaveBeenCalledWith(expect.objectContaining({ tone: "success", title: expect.stringContaining("11:00") }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("409 slot taken: explains in Arabic, clears the choice and reloads the free slots", async () => {
    api.appointments.reschedule.mockRejectedValue(new ApiError({ status: 409, code: "SLOT_TAKEN", message: "server text" }));
    const { onUpdated } = setup();
    const dialog = await openReschedule();
    fireEvent.click(within(dialog).getByLabelText("11:00"));
    await act(async () => fireEvent.click(within(dialog).getByRole("button", { name: m.reschedule.submit })));

    expect(within(dialog).getByRole("alert").textContent).toBe("الموعد المحدد لم يعد متاحًا. اختر وقتًا آخر.");
    await waitFor(() => expect(api.doctors.availability).toHaveBeenCalledTimes(2));
    expect(within(dialog).getByText(m.reschedule.notChosen)).toBeTruthy();
    expect(onUpdated).not.toHaveBeenCalled();
  });

  it("422 outside working hours: shows the server's localized message and keeps the dialog open", async () => {
    const message = "الوقت المختار خارج ساعات عمل الطبيب.";
    api.appointments.reschedule.mockRejectedValue(new ApiError({ status: 422, code: "OUTSIDE_WORKING_HOURS", message }));
    setup();
    const dialog = await openReschedule();
    fireEvent.click(within(dialog).getByLabelText("11:00"));
    await act(async () => fireEvent.click(within(dialog).getByRole("button", { name: m.reschedule.submit })));

    expect(within(dialog).getByRole("alert").textContent).toBe(message);
    expect(screen.getByRole("dialog")).toBeTruthy();
  });

  it("404 (gone or not accessible): closes, reports it and reloads the list", async () => {
    api.appointments.reschedule.mockRejectedValue(new ApiError({ status: 404, code: "APPOINTMENT_NOT_FOUND", message: "الموعد غير موجود." }));
    const { onConflict, onUpdated } = setup();
    const dialog = await openReschedule();
    fireEvent.click(within(dialog).getByLabelText("11:00"));
    await act(async () => fireEvent.click(within(dialog).getByRole("button", { name: m.reschedule.submit })));

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(toast).toHaveBeenCalledWith({ tone: "danger", title: "الموعد غير موجود." });
    expect(onConflict).toHaveBeenCalled();
    expect(onUpdated).not.toHaveBeenCalled();
  });

  it("closing changes nothing", async () => {
    const { onUpdated, onConflict } = setup();
    const dialog = await openReschedule();
    fireEvent.click(within(dialog).getByLabelText("11:00"));
    fireEvent.click(within(dialog).getByRole("button", { name: m.reschedule.keep }));

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(api.appointments.reschedule).not.toHaveBeenCalled();
    expect(onUpdated).not.toHaveBeenCalled();
    expect(onConflict).not.toHaveBeenCalled();
  });

  it("date before today is refused in the form and loads no slots", async () => {
    setup();
    const dialog = await openReschedule();
    api.doctors.availability.mockClear();
    const input = within(dialog).getByLabelText(new RegExp(m.reschedule.date));
    fireEvent.change(input, { target: { value: addDays(todayInZone(ZONE), -1) } });
    expect(input.getAttribute("aria-invalid")).toBe("true");
    expect(within(dialog).getByText(m.reschedule.pastDate)).toBeTruthy();
    expect(api.doctors.availability).not.toHaveBeenCalled();
  });
});

describe("Wording", () => {
  it("Arabic", () => {
    expect(ar.appointments.actions.reschedule).toBe("إعادة جدولة");
    expect(ar.appointments.reschedule.slotTaken).toBe("الموعد المحدد لم يعد متاحًا. اختر وقتًا آخر.");
  });

  it("English, rendered in an LTR dialog", async () => {
    setup(appointment(), "en");
    const dialog = await openReschedule(en);
    expect(dialog.closest("[dir]")?.getAttribute("dir")).toBe("ltr");
    expect(within(dialog).getByText("Current time")).toBeTruthy();
    expect(within(dialog).getByRole("button", { name: "Confirm new time" })).toBeTruthy();
    expect(en.appointments.reschedule.slotTaken).toBe("The selected slot is no longer available. Please choose another time.");
  });
});
