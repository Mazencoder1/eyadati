import { call, unique } from "./support/api";
import { expect, fmt, labelled, messages, signIn, test } from "./support/fixtures";

const ar = messages.ar;

test.describe("Owner", () => {
  test.beforeEach(async ({ page, world }) => {
    await signIn(page, world.owner.email);
  });

  test("lands on Today with the clinic sections in the navigation", async ({ page, world }) => {
    await expect(page.getByRole("heading", { level: 1, name: ar.today.title })).toBeVisible();
    await expect(page.getByText(world.clinicName)).toBeVisible();
    const nav = page.getByRole("navigation", { name: ar.shell.mainNav });
    for (const label of Object.values(ar.nav)) await expect(nav.getByRole("link", { name: label })).toBeVisible();
  });

  test("invites a staff member, who appears as a pending invitation", async ({ page }) => {
    const email = `${unique("invitee")}@example.com`;
    await page.goto("/staff");
    await page.getByRole("button", { name: ar.staff.invite }).click();
    const dialog = page.getByRole("dialog", { name: ar.staff.inviteTitle });
    await dialog.getByLabel(labelled(ar.staff.fields.fullName)).first().fill("سلمى الممرضة");
    await dialog.getByLabel(labelled(ar.staff.fields.email)).first().fill(email);
    await dialog.getByLabel("تمريض").check();
    await dialog.getByRole("button", { name: ar.staff.sendInvitation }).click();
    await expect(page.getByText(ar.staff.invited)).toBeVisible();
    const row = page.getByRole("row").filter({ hasText: email });
    await expect(row.getByText(ar.staff.statuses.Invited)).toBeVisible();
  });

  test("adds a service and sees both branches", async ({ page }) => {
    const name = `استشارة ${unique("s").slice(2, 8)}`;
    await page.goto("/settings");
    await page.getByRole("tab", { name: ar.settings.tabs.services }).click();
    await page.getByRole("button", { name: ar.settings.services.add }).click();
    const dialog = page.getByRole("dialog", { name: ar.settings.services.add });
    await dialog.getByLabel(labelled(ar.settings.services.nameAr)).first().fill(name);
    await dialog.getByLabel(labelled(ar.settings.services.duration)).fill("30");
    await dialog.getByLabel(labelled(ar.settings.services.priceExVat)).fill("150");
    await dialog.getByRole("button", { name: ar.common.save, exact: true }).click();
    await expect(page.getByText(ar.settings.services.saved)).toBeVisible();
    await expect(page.getByRole("cell", { name }).first()).toBeVisible();

    await page.getByRole("tab", { name: ar.settings.tabs.branches }).click();
    await expect(page.getByText("فرع الشمال")).toBeVisible();
    await expect(page.getByText(ar.settings.branches.main).first()).toBeVisible();
  });

  test("sees the clinic's doctors", async ({ page, world }) => {
    await page.goto("/doctors");
    await expect(page.getByText(world.doctorA.displayName)).toBeVisible();
    await expect(page.getByText(world.doctorB.displayName)).toBeVisible();
  });

  test("sees the subscription state without prices", async ({ page }) => {
    await page.goto("/subscription");
    await expect(page.getByRole("heading", { level: 1, name: ar.subscription.title })).toBeVisible();
    await expect(page.getByText("الاحترافية").first()).toBeVisible();
    await expect(page.getByText(ar.subscription.statuses.Active).first()).toBeVisible();
    await expect(page.getByText(ar.subscription.pricesLater).first()).toBeVisible();
  });

  test("role editor hides permissions of features that don't exist, without changing stored roles", async ({ page, world }) => {
    const before = (await call<{ systemKey?: string; permissions: string[] }[]>("GET", "/roles", world.owner.token)).find((r) => r.systemKey === "receptionist")!;
    expect(before.permissions).toContain("Invoices.View");

    await page.goto("/staff");
    await page.getByRole("tab", { name: ar.staff.tabs.roles }).click();
    await page.getByRole("button", { name: fmt(ar.staff.roles.editNamed, { name: "موظف استقبال" }) }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("checkbox", { name: ar.permissions.Patients.View })).toBeVisible();
    await expect(dialog.getByRole("checkbox", { name: ar.permissions.Invoices.View })).toHaveCount(0);
    await dialog.getByRole("button", { name: ar.common.save, exact: true }).click();
    await expect(page.getByText(ar.staff.roles.saved)).toBeVisible();

    const after = (await call<{ systemKey?: string; permissions: string[] }[]>("GET", "/roles", world.owner.token)).find((r) => r.systemKey === "receptionist")!;
    expect([...after.permissions].sort()).toEqual([...before.permissions].sort());
  });

  test("reads the clinic's audit log and filters it", async ({ page }) => {
    await page.getByRole("navigation", { name: ar.shell.mainNav }).getByRole("link", { name: ar.nav.auditLog }).click();
    await expect(page.getByRole("heading", { level: 1, name: ar.audit.title })).toBeVisible();
    const table = page.getByRole("table", { name: ar.audit.title });
    // Newest first: this sign-in is on the first page.
    await expect(table.getByText(ar.audit.actions.auth_login_succeeded).first()).toBeVisible();
    await page.getByLabel(labelled(ar.audit.filters.category)).selectOption("staff.");
    await expect(table.getByText(ar.audit.actions.staff_invited).first()).toBeVisible();
    await expect(table.getByText(ar.audit.actions.auth_login_succeeded)).toHaveCount(0);
  });
});
