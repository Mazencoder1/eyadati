import { describe, expect, it } from "vitest";
import type { StaffMember } from "@/lib/api/types";
import { EMPTY_GUID, isSelf, seatLabel, seatView } from "./seat";

const member = (over: Partial<StaffMember>): StaffMember => ({
  membershipId: "m1",
  userId: "u1",
  fullName: "نورة الشهري",
  email: "noura@example.test",
  status: "Active",
  isOwner: false,
  emailConfirmed: true,
  hasPassword: true,
  roles: [],
  branchIds: [],
  rowVersion: "rv",
  ...over,
});

// What POST /staff and GET /staff return for any pending seat (registered email or not), per T-010.
const pending = member({
  status: "Invited",
  fullName: "new@example.test",
  email: "new@example.test",
  userId: EMPTY_GUID,
  phoneNumber: null,
  lastLoginAtUtc: null,
  emailConfirmed: false,
  hasPassword: false,
});

describe("seatView", () => {
  it("pending seat shows the invited email only — no name, offers resend and revoke", () => {
    expect(seatView(pending)).toEqual({
      pending: true,
      title: "new@example.test",
      subtitle: null,
      canResend: true,
      disableKind: "revoke",
      canReinvite: false,
    });
  });

  it("active member shows name + email; disable is a real disable; no resend", () => {
    expect(seatView(member({}))).toMatchObject({ pending: false, title: "نورة الشهري", subtitle: "noura@example.test", canResend: false, disableKind: "disable" });
  });

  it("an active member without a password is no longer treated as pending (status is the source of truth)", () => {
    expect(seatView(member({ hasPassword: false })).canResend).toBe(false);
  });

  it("disabled seat: enabling re-invites", () => {
    expect(seatView(member({ status: "Disabled" }))).toMatchObject({ disableKind: null, canReinvite: true, canResend: false });
  });
});

describe("isSelf / seatLabel", () => {
  it("never matches a pending seat (masked userId)", () => {
    expect(isSelf(pending, EMPTY_GUID)).toBe(false);
    expect(isSelf(member({}), "u1")).toBe(true);
    expect(isSelf(member({}), undefined)).toBe(false);
  });
  it("labels pending seats by email + status, others by name + email", () => {
    expect(seatLabel(pending, "بانتظار قبول الدعوة")).toBe("new@example.test — بانتظار قبول الدعوة");
    expect(seatLabel(member({}), "x")).toBe("نورة الشهري — noura@example.test");
  });
});
