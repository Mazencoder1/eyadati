import { describe, expect, it } from "vitest";
import { branchChoices, toScheduleWindows } from "@/features/doctors/schedule-editor";
import type { Branch } from "@/lib/api/types";
import { assignableBranches, canCreateBranch, canEditBranch, isBranchRestricted } from "./branch-scope";

const branch = (id: string, isActive = true) => ({ id, name: id, isMain: false, isActive, rowVersion: "rv" }) as Branch;
const all = [branch("b1"), branch("b2"), branch("b3", false)];

describe("branch scope", () => {
  it("empty scope = unrestricted", () => {
    expect(isBranchRestricted([])).toBe(false);
    expect(canCreateBranch([])).toBe(true);
    expect(canEditBranch([], "b2")).toBe(true);
    expect(assignableBranches(all, []).map((b) => b.id)).toEqual(["b1", "b2"]);
  });

  it("restricted member edits only own branches and cannot create branches", () => {
    expect(isBranchRestricted(["b1"])).toBe(true);
    expect(canCreateBranch(["b1"])).toBe(false);
    expect(canEditBranch(["b1"], "b1")).toBe(true);
    expect(canEditBranch(["b1"], "b2")).toBe(false);
    expect(assignableBranches(all, ["b1", "b3"]).map((b) => b.id)).toEqual(["b1"]);
  });
});

describe("schedule editor payload", () => {
  it("sends exactly the editor rows (the server's scoped view) — no hidden windows are added", () => {
    const rows = [
      { key: 1, branchId: "b1", dayOfWeek: "Sunday" as const, start: "09:00", end: "13:00" },
      { key: 2, branchId: "b1", dayOfWeek: "Monday" as const, start: "16:00", end: "21:00" },
    ];
    expect(toScheduleWindows(rows)).toEqual([
      { branchId: "b1", dayOfWeek: "Sunday", startTime: "09:00:00", endTime: "13:00:00" },
      { branchId: "b1", dayOfWeek: "Monday", startTime: "16:00:00", endTime: "21:00:00" },
    ]);
    expect(toScheduleWindows([])).toEqual([]);
  });

  it("branch choices are the assignable branches, keeping a period's current branch visible", () => {
    const assignable = assignableBranches(all, ["b1"]);
    expect(branchChoices(assignable, all, "b1").map((b) => b.id)).toEqual(["b1"]);
    expect(branchChoices(assignable, all, "b3").map((b) => b.id)).toEqual(["b3", "b1"]);
    expect(branchChoices(assignable, all, "").map((b) => b.id)).toEqual(["b1"]);
  });
});
