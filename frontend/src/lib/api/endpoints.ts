import type { ApiClient } from "./client";
import type {
  AuditLogItem,
  FollowUpItem,
  Appointment,
  AppointmentStatus,
  AvailableSlot,
  Branch,
  ClinicService,
  ClinicSettings,
  CreateAppointmentRequest,
  CreatePrescriptionRequest,
  CreateVisitRequest,
  Doctor,
  DuplicateCandidate,
  Guid,
  LoginRequest,
  MeResponse,
  MfaEnrollmentResult,
  MfaSetup,
  PageQuery,
  PatientDetails,
  PatientListItem,
  PatientMedicalInfo,
  PermissionGroup,
  Prescription,
  PrescriptionListItem,
  PublicPlan,
  RecoveryCodesResult,
  RegisterClinicRequest,
  Role,
  SaveBranchRequest,
  SaveDoctorRequest,
  SavePatientRequest,
  ScheduleWindow,
  Session,
  SessionInfo,
  StaffMember,
  SubscriptionView,
  TenantSummary,
  UpdateVisitRequest,
  Visit,
  VisitListItem,
} from "./types";

/**
 * Typed endpoint functions, grouped by domain. UI components call these through hooks; they never build URLs.
 */
export function createEndpoints(api: ApiClient) {
  const withSession = async (p: Promise<Session>) => {
    const session = await p;
    api.setSession(session);
    return session;
  };

  return {
    auth: {
      login: (req: LoginRequest) => withSession(api.post<Session>("/auth/login", req, { anonymous: true })),
      register: (req: RegisterClinicRequest) =>
        withSession(api.post<Session>("/auth/register", req, { anonymous: true })),
      logout: async () => {
        try {
          await api.post("/auth/logout", {}, { anonymous: true });
        } finally {
          api.setSession(null);
        }
      },
      forgotPassword: (email: string) => api.post("/auth/forgot-password", { email }, { anonymous: true }),
      resetPassword: (token: string, newPassword: string) =>
        api.post("/auth/reset-password", { token, newPassword }, { anonymous: true }),
      /**
       * New account: anonymous with newPassword. Existing account: sent with the current session's bearer token (if
       * any) and without a password — the server matches the session to the invited account (T-010).
       */
      acceptInvitation: (token: string, newPassword?: string) =>
        api.post("/auth/accept-invitation", newPassword === undefined ? { token } : { token, newPassword }),
      verifyEmail: (token: string) => api.post("/auth/verify-email", { token }, { anonymous: true }),
      resendVerification: () => api.post("/auth/resend-verification"),
      tenants: () => api.get<TenantSummary[]>("/auth/tenants"),
      switchTenant: (tenantId: Guid) => withSession(api.post<Session>("/auth/switch-tenant", { tenantId })),
      me: () => api.get<MeResponse>("/auth/me"),
      /** Ends every session (this one too) and returns a fresh session for this device. */
      changePassword: (currentPassword: string, newPassword: string) =>
        withSession(api.post<Session>("/auth/change-password", { currentPassword, newPassword })),
      sessions: (signal?: AbortSignal) => api.get<SessionInfo[]>("/auth/sessions", { signal }),
      revokeSession: (sessionId: Guid) => api.delete(`/auth/sessions/${sessionId}`),
      /** Platform-admin MFA (TOTP + recovery codes). Login answers 409 MFA_REQUIRED / MFA_ENROLLMENT_REQUIRED first. */
      mfa: {
        /** Second step of sign-in: exactly one of `totpCode` / `recoveryCode`. Creates the session. */
        verify: (challengeToken: string, code: { totpCode: string } | { recoveryCode: string }) =>
          withSession(api.post<Session>("/auth/mfa/verify", { challengeToken, ...code }, { anonymous: true })),
        enrollStart: (enrollmentToken: string) =>
          api.post<MfaSetup>("/auth/mfa/enroll/start", { enrollmentToken }, { anonymous: true }),
        /**
         * Confirms the authenticator. Deliberately does NOT adopt the returned session: the recovery codes must be
         * shown first (the sign-in page redirects as soon as a session exists). Call {@link adoptSession} afterwards.
         */
        enrollComplete: (pendingToken: string, code: string) =>
          api.post<MfaEnrollmentResult>("/auth/mfa/enroll/complete", { pendingToken, code }, { anonymous: true }),
        adoptSession: (session: Session) => api.setSession(session),
        /** Needs a current TOTP (a recovery code cannot do this). Replaces all ten codes. */
        regenerateRecoveryCodes: (totpCode: string) =>
          api.post<RecoveryCodesResult>("/auth/mfa/recovery-codes/regenerate", { totpCode }),
        /** Removes MFA and ends every session including this one: nothing is left to keep. */
        remove: async (currentPassword: string, code: { totpCode: string } | { recoveryCode: string }) => {
          await api.post("/auth/mfa/remove", { currentPassword, ...code });
          api.setSession(null);
        },
      },
    },

    settings: {
      get: () => api.get<ClinicSettings>("/settings"),
      update: (body: Omit<ClinicSettings, "id" | "slug" | "status" | "currency">) =>
        api.put<ClinicSettings>("/settings", body),
    },

    branches: {
      list: () => api.get<Branch[]>("/branches"),
      create: (body: SaveBranchRequest) => api.post<Branch>("/branches", body),
      update: (id: Guid, body: SaveBranchRequest) => api.put<Branch>(`/branches/${id}`, body),
    },

    services: {
      list: (activeOnly = false) => api.get<ClinicService[]>("/services", { query: { activeOnly } }),
      create: (body: Omit<ClinicService, "id" | "rowVersion">) => api.post<ClinicService>("/services", body),
      update: (id: Guid, body: Omit<ClinicService, "id">) => api.put<ClinicService>(`/services/${id}`, body),
      remove: (id: Guid) => api.delete(`/services/${id}`),
    },

    roles: {
      catalog: () => api.get<PermissionGroup[]>("/permissions"),
      list: () => api.get<Role[]>("/roles"),
      create: (body: { name: string; description?: string | null; permissions: string[] }) =>
        api.post<Role>("/roles", body),
      update: (id: Guid, body: { name: string; description?: string | null; permissions: string[]; rowVersion: string }) =>
        api.put<Role>(`/roles/${id}`, body),
      remove: (id: Guid) => api.delete(`/roles/${id}`),
    },

    staff: {
      list: (q: PageQuery & { status?: string; roleId?: Guid }, signal?: AbortSignal) =>
        api.getPaged<StaffMember>("/staff", { query: { ...q }, signal }),
      invite: (body: { fullName: string; email: string; phoneNumber?: string | null; roleIds: Guid[]; branchIds: Guid[] }) =>
        api.post<StaffMember>("/staff", body),
      update: (id: Guid, body: { roleIds: Guid[]; branchIds: Guid[]; rowVersion: string }) =>
        api.put<StaffMember>(`/staff/${id}`, body),
      disable: (id: Guid) => api.post(`/staff/${id}/disable`),
      enable: (id: Guid) => api.post(`/staff/${id}/enable`),
      resendInvitation: (id: Guid) => api.post(`/staff/${id}/resend-invitation`),
    },

    patients: {
      list: (q: PageQuery & { gender?: string }, signal?: AbortSignal) =>
        api.getPaged<PatientListItem>("/patients", { query: { ...q }, signal }),
      get: (id: Guid, signal?: AbortSignal) => api.get<PatientDetails>(`/patients/${id}`, { signal }),
      duplicates: (mobile?: string, identityNumber?: string, signal?: AbortSignal) =>
        api.get<DuplicateCandidate[]>("/patients/duplicates", { query: { mobile, identityNumber }, signal }),
      create: (body: SavePatientRequest) => api.post<PatientDetails>("/patients", body),
      update: (id: Guid, body: SavePatientRequest & { rowVersion: string }) =>
        api.put<PatientDetails>(`/patients/${id}`, body),
      updateMedical: (id: Guid, body: PatientMedicalInfo & { rowVersion: string }) =>
        api.put<PatientDetails>(`/patients/${id}/medical`, body),
      remove: (id: Guid) => api.delete(`/patients/${id}`),
    },

    doctors: {
      list: (q: PageQuery & { activeOnly?: boolean; branchId?: Guid }, signal?: AbortSignal) =>
        api.getPaged<Doctor>("/doctors", { query: { ...q }, signal }),
      get: (id: Guid, signal?: AbortSignal) => api.get<Doctor>(`/doctors/${id}`, { signal }),
      create: (body: SaveDoctorRequest & { membershipId: Guid; schedule?: ScheduleWindow[] }) =>
        api.post<Doctor>("/doctors", body),
      update: (id: Guid, body: SaveDoctorRequest & { isActive: boolean; rowVersion: string }) =>
        api.put<Doctor>(`/doctors/${id}`, body),
      updateSchedule: (id: Guid, windows: ScheduleWindow[], rowVersion: string) =>
        api.put<Doctor>(`/doctors/${id}/schedule`, { windows, rowVersion }),
      availability: (id: Guid, q: { date: string; serviceId?: Guid; branchId?: Guid }, signal?: AbortSignal) =>
        api.get<AvailableSlot[]>(`/doctors/${id}/availability`, { query: { ...q }, signal }),
    },

    appointments: {
      list: (
        q: PageQuery & { date?: string; doctorId?: Guid; branchId?: Guid; patientId?: Guid; status?: AppointmentStatus },
        signal?: AbortSignal,
      ) => api.getPaged<Appointment>("/appointments", { query: { ...q }, signal }),
      create: (body: CreateAppointmentRequest) => api.post<Appointment>("/appointments", body),
      reschedule: (id: Guid, body: { startUtc: string; doctorId?: Guid; branchId?: Guid; rowVersion: string }) =>
        api.put<Appointment>(`/appointments/${id}/reschedule`, body),
      changeStatus: (id: Guid, action: AppointmentAction, rowVersion: string, reason?: string) =>
        api.post<Appointment>(`/appointments/${id}/${action}`, { rowVersion, reason }),
    },

    visits: {
      list: (q: PageQuery & { appointmentId?: Guid; doctorId?: Guid; patientId?: Guid }, signal?: AbortSignal) =>
        api.getPaged<VisitListItem>("/visits", { query: { ...q }, signal }),
      listForPatient: (patientId: Guid, q: PageQuery, signal?: AbortSignal) =>
        api.getPaged<VisitListItem>(`/patients/${patientId}/visits`, { query: { ...q }, signal }),
      get: (id: Guid, signal?: AbortSignal) => api.get<Visit>(`/visits/${id}`, { signal }),
      create: (body: CreateVisitRequest) => api.post<Visit>("/visits", body),
      update: (id: Guid, body: UpdateVisitRequest) => api.put<Visit>(`/visits/${id}`, body),
      complete: (id: Guid, rowVersion: string) => api.post<Visit>(`/visits/${id}/complete`, { rowVersion }),
    },

    prescriptions: {
      listForPatient: (patientId: Guid, q: PageQuery, signal?: AbortSignal) =>
        api.getPaged<PrescriptionListItem>(`/patients/${patientId}/prescriptions`, { query: { ...q }, signal }),
      get: (id: Guid, signal?: AbortSignal) => api.get<Prescription>(`/prescriptions/${id}`, { signal }),
      create: (body: CreatePrescriptionRequest) => api.post<Prescription>("/prescriptions", body),
    },

    subscription: {
      current: () => api.get<SubscriptionView | null>("/subscription"),
      plans: () => api.get<PublicPlan[]>("/plans", { anonymous: true }),
    },

    followUps: {
      /** Follow-ups due in [from, to] (clinic-local dates; server default today … +7 days). */
      list: (
        q: Omit<PageQuery, "search" | "sort"> & { from?: string; to?: string; doctorId?: Guid; booked?: boolean },
        signal?: AbortSignal,
      ) => api.getPaged<FollowUpItem>("/follow-ups", { query: { ...q }, signal }),
    },

    auditLogs: {
      /** Newest first. `action` filters by prefix (e.g. "patient." or "auth."); the time range is [fromUtc, toUtc). */
      list: (q: Omit<PageQuery, "search" | "sort"> & { action?: string; userId?: Guid; fromUtc?: string; toUtc?: string }, signal?: AbortSignal) =>
        api.getPaged<AuditLogItem>("/audit-logs", { query: { ...q }, signal }),
    },
  };
}

export type AppointmentAction = "confirm" | "check-in" | "complete" | "cancel" | "no-show";

export type Endpoints = ReturnType<typeof createEndpoints>;
