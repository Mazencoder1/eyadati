import { call, login, mailboxToken, mobile, nationalId, PASSWORD, platformLogin, unique, type SessionData } from "./api";

/**
 * One clinic with the people the core workflows need, built through the public API (no database access):
 * owner, two doctors, a receptionist, a receptionist restricted to the second branch, plus an unrelated clinic.
 * The clinic is moved to the "professional" plan through the platform API (needs E2E_PLATFORM_* — see README).
 */
export interface Person {
  email: string;
  name: string;
  token: string;
  membershipId: string;
}

export interface World {
  clinicName: string;
  tenantId: string;
  owner: Person;
  doctorA: Person & { doctorId: string; displayName: string };
  doctorB: Person & { doctorId: string; displayName: string };
  reception: Person;
  receptionNorth: Person;
  mainBranchId: string;
  northBranchId: string;
  other: Person;
  /** A visit documented by doctor A (open), with an issued prescription; the patient has an allergy on file. */
  sample: { patientId: string; patientName: string; visitId: string; prescriptionId: string; prescriptionNumber: string; diagnosis: string };
}

const ALL_DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

async function register(clinicName: string, ownerName: string): Promise<{ person: Person; tenantId: string }> {
  const email = `${unique("owner")}@example.com`;
  const s = await call<SessionData>("POST", "/auth/register", null, {
    clinicName, city: "الرياض", clinicPhone: mobile(), ownerFullName: ownerName, email, password: PASSWORD, ownerMobile: mobile(), acceptTerms: true,
  });
  return { person: { email, name: ownerName, token: s.accessToken, membershipId: "" }, tenantId: s.tenant!.id };
}

async function invite(owner: string, name: string, roleKey: string, branchIds: string[] = []): Promise<Person> {
  const roles = await call<{ id: string; systemKey?: string }[]>("GET", "/roles", owner);
  const role = roles.find((r) => r.systemKey === roleKey);
  if (!role) throw new Error(`No system role ${roleKey}`);
  const email = `${unique(roleKey)}@example.com`;
  const seat = await call<{ membershipId: string }>("POST", "/staff", owner, { fullName: name, email, roleIds: [role.id], branchIds });
  const token = await mailboxToken(email, "/accept-invitation");
  await call("POST", "/auth/accept-invitation", null, { token, newPassword: PASSWORD });
  return { email, name, token: await login(email), membershipId: seat.membershipId };
}

async function movePlan(tenantId: string, planCode: string) {
  const admin = await platformLogin(); // signs in with MFA (enrols the local super admin on first use)
  const current = await call<{ rowVersion: string } | null>("GET", `/platform/tenants/${tenantId}/subscription`, admin);
  await call("PUT", `/platform/tenants/${tenantId}/subscription`, admin, {
    planCode, status: "Active", billingCycle: "Monthly",
    currentPeriodEndUtc: new Date(Date.now() + 30 * 86_400_000).toISOString(), rowVersion: current?.rowVersion ?? null,
  });
}

export async function createPatient(token: string, name: string): Promise<{ id: string; fullName: string }> {
  return call("POST", "/patients", token, { fullName: name, mobileNumber: mobile(), identityType: "NationalId", identityNumber: nationalId(), gender: "Female" });
}

export async function buildWorld(): Promise<World> {
  const clinicName = `عيادة الاختبار ${unique("c").slice(2)}`;
  const { person: owner, tenantId } = await register(clinicName, "منى المالكة");
  await movePlan(tenantId, "professional");

  const branches = await call<{ id: string; isMain: boolean }[]>("GET", "/branches", owner.token);
  const mainBranchId = branches.find((b) => b.isMain)!.id;
  const north = await call<{ id: string }>("POST", "/branches", owner.token, { name: "فرع الشمال", city: "الرياض" });

  const schedule = ALL_DAYS.map((d) => ({ branchId: mainBranchId, dayOfWeek: d, startTime: "08:00:00", endTime: "22:00:00" }));
  const doctor = async (name: string, displayName: string) => {
    const person = await invite(owner.token, name, "doctor");
    const profile = await call<{ id: string }>("POST", "/doctors", owner.token, {
      membershipId: person.membershipId, displayNameAr: displayName, specialty: "طب الأسرة", consultationFee: 150,
      defaultSlotMinutes: 15, acceptsOnlineBooking: false, schedule,
    });
    // The doctor identity is resolved from the session; sign in again so the token is fresh.
    return { ...person, token: await login(person.email), doctorId: profile.id, displayName };
  };
  const doctorA = await doctor("د. ليلى الحربي", "د. ليلى الحربي");
  const doctorB = await doctor("د. فهد العتيبي", "د. فهد العتيبي");
  const reception = await invite(owner.token, "نوال الاستقبال", "receptionist");
  const receptionNorth = await invite(owner.token, "هيا استقبال الشمال", "receptionist", [north.id]);
  const { person: other } = await register(`عيادة أخرى ${unique("o").slice(2)}`, "مالك آخر");

  // Clinical sample: walk-in → check-in → doctor A documents and prescribes.
  const patient = await createPatient(owner.token, "ريم الزهراني");
  const p = await call<{ rowVersion: string }>("GET", `/patients/${patient.id}`, owner.token);
  await call("PUT", `/patients/${patient.id}/medical`, owner.token, { allergies: "بنسلين", chronicConditions: "ربو", rowVersion: p.rowVersion });
  const walkIn = await call<{ id: string; rowVersion: string }>("POST", "/appointments", reception.token, {
    patientId: patient.id, doctorId: doctorA.doctorId, branchId: mainBranchId, source: "WalkIn",
  });
  await call("POST", `/appointments/${walkIn.id}/check-in`, reception.token, { rowVersion: walkIn.rowVersion });
  const visit = await call<{ id: string; rowVersion: string }>("POST", "/visits", doctorA.token, {
    patientId: patient.id, doctorId: doctorA.doctorId, appointmentId: walkIn.id,
  });
  const diagnosis = "التهاب الحلق";
  await call("PUT", `/visits/${visit.id}`, doctorA.token, {
    clinical: { symptoms: "ألم في الحلق", diagnosis, diagnosisCode: "J02.9", treatmentPlan: "راحة وسوائل" }, rowVersion: visit.rowVersion,
  });
  const rx = await call<{ id: string; number: string }>("POST", "/prescriptions", doctorA.token, {
    visitId: visit.id, items: [{ medicationName: "Paracetamol", strength: "500mg", dosage: "قرص واحد", frequency: "3 مرات يومياً", durationDays: 3 }],
  });

  return {
    clinicName, tenantId, owner, doctorA, doctorB, reception, receptionNorth, mainBranchId, northBranchId: north.id, other,
    sample: { patientId: patient.id, patientName: patient.fullName, visitId: visit.id, prescriptionId: rx.id, prescriptionNumber: rx.number, diagnosis },
  };
}
