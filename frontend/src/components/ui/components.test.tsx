import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import ar from "../../../messages/ar.json";
import { mapServerErrors } from "@/lib/hooks/use-form";
import { Badge } from "./badge";
import { DataTable, formatSort, parseSort } from "./data-table";
import { Input } from "./field";
import { pageWindow } from "./navigation";

afterEach(cleanup);

const wrap = (ui: ReactNode) =>
  render(
    <NextIntlClientProvider locale="ar" messages={ar} timeZone="Asia/Riyadh">
      <div dir="rtl">{ui}</div>
    </NextIntlClientProvider>,
  );

describe("Badge", () => {
  it("conveys status with text, not color alone", () => {
    wrap(<Badge tone="success" icon={<svg data-testid="icon" />}>مكتمل</Badge>);
    expect(screen.getByText("مكتمل")).toBeTruthy();
    expect(screen.getByTestId("icon").parentElement?.getAttribute("aria-hidden")).toBe("true");
  });
});

describe("Input", () => {
  it("links label, error and aria-invalid", () => {
    wrap(<Input label="رقم الجوال" error="رقم الجوال غير صحيح" defaultValue="123" />);
    const input = screen.getByLabelText("رقم الجوال");
    expect(input.getAttribute("aria-invalid")).toBe("true");
    const describedBy = input.getAttribute("aria-describedby")!;
    expect(document.getElementById(describedBy)?.textContent).toBe("رقم الجوال غير صحيح");
  });
});

describe("DataTable", () => {
  const columns = [
    { id: "name", header: "الاسم", cell: (r: { id: string; name: string }) => r.name, sortKey: "fullName" },
    { id: "mrn", header: "رقم الملف", cell: (r: { id: string; name: string }) => r.id, hideable: true },
  ];

  it("renders rows with mobile labels and reports sort changes", () => {
    const onSortChange = vi.fn();
    wrap(
      <DataTable
        caption="المرضى"
        columns={columns}
        rows={[{ id: "P-1", name: "سارة" }]}
        rowKey={(r) => r.id}
        onSortChange={onSortChange}
      />,
    );
    const table = screen.getByRole("table", { name: "المرضى" });
    expect(within(table).getByText("سارة").closest("td")?.getAttribute("data-label")).toBe("الاسم");
    fireEvent.click(within(table).getByRole("button", { name: /الاسم/ }));
    expect(onSortChange).toHaveBeenCalledWith({ key: "fullName", desc: false });
    expect(within(table).getByRole("columnheader", { name: /الاسم/ }).getAttribute("aria-sort")).toBe("none");
  });

  it("shows the empty state and the error state with retry", () => {
    const onRetry = vi.fn();
    const { rerender } = wrap(<DataTable caption="x" columns={columns} rows={[]} rowKey={(r) => r.id} />);
    expect(screen.getByText(ar.common.table.empty)).toBeTruthy();

    rerender(
      <NextIntlClientProvider locale="ar" messages={ar} timeZone="Asia/Riyadh">
        <DataTable caption="x" columns={columns} rows={undefined} rowKey={(r) => r.id} error="تعذر الاتصال" onRetry={onRetry} />
      </NextIntlClientProvider>,
    );
    expect(screen.getByRole("alert").textContent).toContain("تعذر الاتصال");
    fireEvent.click(screen.getByRole("button", { name: ar.common.table.retry }));
    expect(onRetry).toHaveBeenCalled();
  });

  it("lets the user hide optional columns", () => {
    wrap(<DataTable caption="x" columns={columns} rows={[{ id: "P-1", name: "سارة" }]} rowKey={(r) => r.id} />);
    fireEvent.click(screen.getByRole("button", { name: ar.common.table.columns }));
    fireEvent.click(screen.getByRole("checkbox", { name: "رقم الملف" }));
    expect(screen.queryByText("P-1")).toBeNull();
  });
});

describe("helpers", () => {
  it("parses and formats server sort strings", () => {
    expect(parseSort("-createdAt")).toEqual({ key: "createdAt", desc: true });
    expect(formatSort({ key: "fullName", desc: false })).toBe("fullName");
    expect(formatSort(undefined)).toBeUndefined();
  });

  it("builds a compact page window", () => {
    expect(pageWindow(1, 3)).toEqual([1, 2, 3]);
    expect(pageWindow(10, 20)).toEqual([1, "gap", 9, 10, 11, "gap", 20]);
  });

  it("maps server field errors to the first message per field", () => {
    expect(mapServerErrors({ mobileNumber: ["a", "b"], email: [] })).toEqual({ mobileNumber: "a" });
  });
});
