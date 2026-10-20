import fs from "node:fs";
import path from "node:path";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { ApiError } from "@/lib/api/client";
import type { AuditLogItem } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { AuditLogPage } from "./audit-log-page";
import { actionMessageKey, describeDetails } from "./audit-model";

const rows: AuditLogItem[] = [
  { id: 2, occurredAtUtc: "2026-10-08T06:30:00Z", action: "patient.updated", entityType: "Patient", entityId: "p1", result: "Success", userId: "u1", userName: "نوال", ipAddress: "10.0.0.5", details: '{"fields":["MobileNumber"]}' },
  { id: 1, occurredAtUtc: "2026-10-08T05:00:00Z", action: "security.access_denied", entityType: null, result: "Denied", userId: "u2", userName: "هيا", ipAddress: "10.0.0.6", details: null },
  { id: 0, occurredAtUtc: "2026-10-07T05:00:00Z", action: "some.future_action", entityType: "Unknown", result: "Failure", userId: null, userName: null },
];

const api = {
  auditLogs: { list: vi.fn() },
  staff: { list: vi.fn() },
};
let permissions: string[] = [P.AuditLogs.View, P.Staff.View];
const can = (p: string) => permissions.includes(p);
const canAny = (...p: string[]) => p.some(can);

vi.mock("@/lib/auth/session", () => ({ useAuth: () => ({ api, can, canAny }), useApi: () => api }));
vi.mock("@/features/clinic/clinic-context", () => ({ useClinic: () => ({ timeZone: "Asia/Riyadh" }) }));

beforeEach(() => {
  permissions = [P.AuditLogs.View, P.Staff.View];
  api.auditLogs.list.mockReset().mockResolvedValue({ items: rows, meta: { page: 1, pageSize: 25, totalCount: 3, totalPages: 1 } });
  api.staff.list.mockReset().mockResolvedValue({
    items: [
      { userId: "u1", fullName: "نوال" },
      { userId: "00000000-0000-0000-0000-000000000000", fullName: "invited@x.sa" },
    ],
  });
});
afterEach(cleanup);

function setup(locale: "ar" | "en" = "ar") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Riyadh">
      <AuditLogPage />
    </NextIntlClientProvider>,
  );
}

const lastQuery = () => api.auditLogs.list.mock.calls.at(-1)![0];

describe("audit helpers", () => {
  it("maps action codes to message keys and summarizes details", () => {
    expect(actionMessageKey("auth.login.failed")).toBe("auth_login_failed");
    expect(describeDetails('{"fields":["MobileNumber","Email"]}')).toBe("fields: MobileNumber, Email");
    expect(describeDetails('{"fromUtc":"2026-10-08T07:00:00Z","doctorChanged":false}')).toBe("fromUtc: 2026-10-08T07:00:00Z · doctorChanged: false");
    expect(describeDetails("not json")).toBe("not json");
    expect(describeDetails(null)).toBe("");
  });

  it("has an Arabic and English label for every audit action the backend records", () => {
    const source = fs.readFileSync(path.resolve(__dirname, "../../../../backend/src/ClinicSaaS.Domain/Auditing/AuditLog.cs"), "utf8");
    const codes = [...source.matchAll(/public const string \w+ = "([^"]+)";/g)].map((m) => m[1]);
    expect(codes.length).toBeGreaterThan(40);
    const missing = codes.filter((c) => !(actionMessageKey(c) in ar.audit.actions) || !(actionMessageKey(c) in en.audit.actions));
    expect(missing).toEqual([]);
  });
});

describe("Audit log page", () => {
  it("lists events with readable action, entity, result and details; unknown codes fall back to the code", async () => {
    setup();
    const table = await screen.findByRole("table", { name: ar.audit.title });
    expect(within(table).getByText("تعديل بيانات مريض")).toBeTruthy();
    expect(within(table).getByText("مريض")).toBeTruthy();
    // IP and details are secondary: off by default, available from the column menu.
    expect(within(table).queryByText("fields: MobileNumber")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: ar.common.table.columns }));
    fireEvent.click(screen.getByRole("checkbox", { name: ar.audit.columns.details }));
    expect(within(table).getByText("fields: MobileNumber")).toBeTruthy();
    expect(within(table).getByText("رفض وصول")).toBeTruthy();
    expect(within(table).getByText(ar.audit.results.Denied)).toBeTruthy();
    expect(within(table).getByText("some.future_action")).toBeTruthy();
    expect(within(table).getByText(ar.audit.system)).toBeTruthy();
    expect(lastQuery()).toMatchObject({ page: 1, pageSize: 25, action: undefined, userId: undefined, fromUtc: undefined, toUtc: undefined });
  });

  it("filters by category, user and a clinic-time date range", async () => {
    setup();
    await screen.findByRole("table");
    await act(async () => fireEvent.change(screen.getByLabelText(ar.audit.filters.category), { target: { value: "patient." } }));
    expect(lastQuery()).toMatchObject({ action: "patient." });

    const userSelect = screen.getByLabelText(ar.audit.filters.user);
    expect(within(userSelect).queryByText("invited@x.sa")).toBeNull(); // pending invitations have no account yet
    await act(async () => fireEvent.change(userSelect, { target: { value: "u1" } }));
    expect(lastQuery()).toMatchObject({ userId: "u1" });

    await act(async () => fireEvent.change(screen.getByLabelText(ar.audit.filters.from), { target: { value: "2026-10-01" } }));
    await act(async () => fireEvent.change(screen.getByLabelText(ar.audit.filters.to), { target: { value: "2026-10-08" } }));
    expect(lastQuery()).toMatchObject({ fromUtc: "2026-09-30T21:00:00.000Z", toUtc: "2026-10-08T21:00:00.000Z", page: 1 });

    await act(async () => fireEvent.click(screen.getByRole("button", { name: ar.audit.filters.clear })));
    expect(lastQuery()).toMatchObject({ action: undefined, userId: undefined, fromUtc: undefined, toUtc: undefined });
  });

  it("refuses an end date before the start date without querying", async () => {
    setup();
    await screen.findByRole("table");
    await act(async () => fireEvent.change(screen.getByLabelText(ar.audit.filters.from), { target: { value: "2026-10-08" } }));
    const calls = api.auditLogs.list.mock.calls.length;
    await act(async () => fireEvent.change(screen.getByLabelText(ar.audit.filters.to), { target: { value: "2026-10-01" } }));
    const to = screen.getByLabelText(ar.audit.filters.to);
    expect(to.getAttribute("aria-invalid")).toBe("true");
    expect(screen.getByText(ar.audit.filters.rangeInvalid)).toBeTruthy();
    expect(api.auditLogs.list.mock.calls.length).toBe(calls);
  });

  it("hides the user filter without Staff.View", async () => {
    permissions = [P.AuditLogs.View];
    setup();
    await screen.findByRole("table");
    expect(screen.queryByLabelText(ar.audit.filters.user)).toBeNull();
    expect(api.staff.list).not.toHaveBeenCalled();
  });

  it("shows the empty state and the error state", async () => {
    api.auditLogs.list.mockResolvedValue({ items: [], meta: { page: 1, pageSize: 25, totalCount: 0, totalPages: 0 } });
    const { unmount } = setup();
    expect(await screen.findByText(ar.audit.emptyTitle)).toBeTruthy();
    unmount();

    api.auditLogs.list.mockRejectedValue(new ApiError({ status: 0, code: "NETWORK_ERROR", message: "تعذر الاتصال بالخادم." }));
    setup();
    expect((await screen.findByRole("alert")).textContent).toContain("تعذر الاتصال بالخادم.");
  });

  it("is a no-permission screen for members without AuditLogs.View", () => {
    permissions = [P.Staff.View];
    setup();
    expect(screen.getByText(ar.shell.forbiddenTitle)).toBeTruthy();
    expect(api.auditLogs.list).not.toHaveBeenCalled();
  });

  it("renders in English", async () => {
    setup("en");
    const table = await screen.findByRole("table", { name: "Audit log" });
    expect(within(table).getByText("Patient updated")).toBeTruthy();
    expect(within(table).getByText("Access denied")).toBeTruthy();
  });
});
