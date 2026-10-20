/** DTOs mirrored from the backend (backend/src/ClinicSaaS.Application). Dates are ISO strings in UTC ("…Z"). */

export type Guid = string;

export interface PageMeta {
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface Paged<T> {
  items: T[];
  meta: PageMeta;
}

export interface PageQuery {
  page?: number;
  pageSize?: number;
  search?: string;
  sort?: string;
}

// ------------------------------------------------------------------ auth

export interface UserSummary {
  id: Guid;
  fullName: string;
  email: string;
  emailConfirmed: boolean;
  platformRole: string;
  preferredLocale: string;
}

export interface TenantSummary {
  id: Guid;
  name: string;
  slug: string;
}

export interface Session {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  user: UserSummary;
  tenant?: TenantSummary | null;
  permissions: string[];
}

// ------------------------------------------------------------------ platform-admin MFA

/** `details` of the 409 MFA_REQUIRED login answer: password was right, a code is still needed. */
export interface MfaChallengeDetails {
  challengeToken: string;
  expiresAtUtc: string;
}

/** `details` of the 409 MFA_ENROLLMENT_REQUIRED login answer: the account must set up an authenticator first. */
export interface MfaEnrollmentDetails {
  enrollmentToken: string;
  expiresAtUtc: string;
}

/** The setup information — shown once and never stored by the client. */
export interface MfaSetup {
  /** Base32 setup key for manual entry. */
  secret: string;
  /** `otpauth://` link (what a QR code would contain). */
  setupUri: string;
  /** Carries the not-yet-confirmed secret back to the server to complete the enrolment. */
  pendingToken: string;
  expiresAtUtc: string;
}

/** The first session of the account plus its 10 one-time recovery codes (shown once). */
export interface MfaEnrollmentResult {
  session: Session;
  recoveryCodes: string[];
}

export interface RecoveryCodesResult {
  recoveryCodes: string[];
}

/** A follow-up due (GET /follow-ups): operational fields only — never diagnosis, notes or any clinical content. */
export interface FollowUpItem {
  patientId: Guid;
  patientName: string;
  patientMrn: string;
  /** Clinic-local date (YYYY-MM-DD) the doctor asked to see the patient again. */
  followUpDate: string;
  doctorId: Guid;
  doctorName: string;
  branchId: Guid;
  /** The patient already has an appointment after that visit. */
  booked: boolean;
  nextAppointmentUtc?: string | null;
}

/** One entry of the clinic's audit trail (GET /audit-logs). Never contains passwords, tokens or clinical text. */
export interface AuditLogItem {
  id: number;
  occurredAtUtc: string;
  /** e.g. "patient.created", "auth.login.failed" */
  action: string;
  entityType?: string | null;
  entityId?: string | null;
  result: "Success" | "Failure" | "Denied";
  userId?: Guid | null;
  userName?: string | null;
  ipAddress?: string | null;
  /** Small JSON context, e.g. {"fields":["MobileNumber"]}. */
  details?: string | null;
}

/** One signed-in device (a refresh-token family) of the current user, from GET /auth/sessions. */
export interface SessionInfo {
  sessionId: Guid;
  /** Clinic the session is signed in to; null for a platform session. */
  tenantId?: Guid | null;
  lastActivityAtUtc: string;
  ipAddress?: string | null;
  userAgent?: string | null;
  isCurrent: boolean;
}

export interface MeResponse {
  userId: Guid;
  tenantId?: Guid | null;
  platformRole: string;
  permissions: string[];
  branchIds: Guid[];
  /** The caller's seat in the current clinic (from the token). */
  membershipId?: Guid | null;
  /** The caller's own doctor profile in the current clinic, resolved by the server; absent for non-doctors. */
  doctorId?: Guid | null;
  /** Clinic sessions only: "ReadOnly" once the subscription period ended, "Suspended" when the platform suspended it. */
  access?: ClinicAccess | null;
}

export type ClinicAccess = "Full" | "ReadOnly" | "Suspended";

export interface RegisterClinicRequest {
  clinicName: string;
  slug?: string | null;
  city: string;
  clinicPhone: string;
  ownerFullName: string;
  email: string;
  password: string;
  ownerMobile?: string | null;
  acceptTerms: boolean;
}

export interface LoginRequest {
  email: string;
  password: string;
  tenantId?: Guid;
}

// ------------------------------------------------------------------ organization

export interface ClinicSettings {
  id: Guid;
  name: string;
  slug: string;
  status: string;
  legalName?: string | null;
  vatNumber?: string | null;
  commercialRegistration?: string | null;
  email?: string | null;
  phone?: string | null;
  city?: string | null;
  timeZone: string;
  currency: string;
  locale: string;
  defaultAppointmentMinutes: number;
  rowVersion: string;
}

export interface Branch {
  id: Guid;
  name: string;
  city?: string | null;
  address?: string | null;
  phone?: string | null;
  latitude?: number | null;
  longitude?: number | null;
  isMain: boolean;
  isActive: boolean;
  rowVersion: string;
}

export interface SaveBranchRequest {
  name: string;
  city?: string | null;
  address?: string | null;
  phone?: string | null;
  latitude?: number | null;
  longitude?: number | null;
  isActive: boolean;
  rowVersion?: string | null;
}

export interface ClinicService {
  id: Guid;
  nameAr: string;
  nameEn?: string | null;
  durationMinutes: number;
  price: number;
  isActive: boolean;
  isBookableOnline: boolean;
  rowVersion: string;
}

export interface PermissionGroup {
  group: string;
  permissions: string[];
}

export interface Role {
  id: Guid;
  name: string;
  description?: string | null;
  systemKey?: string | null;
  isProtected: boolean;
  permissions: string[];
  memberCount: number;
  rowVersion: string;
}

export type MembershipStatus = "Invited" | "Active" | "Disabled";

export interface StaffMember {
  membershipId: Guid;
  userId: Guid;
  fullName: string;
  email: string;
  phoneNumber?: string | null;
  status: MembershipStatus;
  isOwner: boolean;
  emailConfirmed: boolean;
  hasPassword: boolean;
  roles: { id: Guid; name: string }[];
  branchIds: Guid[];
  doctorId?: Guid | null;
  lastLoginAtUtc?: string | null;
  rowVersion: string;
}

// ------------------------------------------------------------------ clinical

export type Gender = "Male" | "Female";
export type IdentityDocumentType = "None" | "NationalId" | "Iqama" | "Passport" | "GccId";
export type BloodGroup =
  | "APositive"
  | "ANegative"
  | "BPositive"
  | "BNegative"
  | "ABPositive"
  | "ABNegative"
  | "OPositive"
  | "ONegative";

export interface PatientListItem {
  id: Guid;
  medicalRecordNumber: string;
  fullName: string;
  mobileNumber: string;
  gender: Gender;
  dateOfBirth?: string | null;
  createdAtUtc: string;
}

export interface PatientMedicalInfo {
  bloodGroup?: BloodGroup | null;
  allergies?: string | null;
  chronicConditions?: string | null;
  medicalNotes?: string | null;
}

export interface PatientDetails {
  id: Guid;
  medicalRecordNumber: string;
  fullName: string;
  fullNameEn?: string | null;
  mobileNumber: string;
  email?: string | null;
  identityType: IdentityDocumentType;
  identityNumber?: string | null;
  dateOfBirth?: string | null;
  gender: Gender;
  nationality?: string | null;
  notes?: string | null;
  /** Present only when the caller holds MedicalRecords.View. */
  medical?: PatientMedicalInfo | null;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
  rowVersion: string;
}

export interface SavePatientRequest {
  fullName: string;
  fullNameEn?: string | null;
  mobileNumber: string;
  email?: string | null;
  identityType: IdentityDocumentType;
  identityNumber?: string | null;
  dateOfBirth?: string | null;
  gender: Gender;
  nationality?: string | null;
  notes?: string | null;
  registeredBranchId?: Guid | null;
  rowVersion?: string;
}

export interface DuplicateCandidate {
  id: Guid;
  medicalRecordNumber: string;
  fullName: string;
  mobileNumber: string;
}

export type DayOfWeek = "Sunday" | "Monday" | "Tuesday" | "Wednesday" | "Thursday" | "Friday" | "Saturday";

export interface ScheduleWindow {
  branchId: Guid;
  dayOfWeek: DayOfWeek;
  /** "HH:mm:ss" clinic-local */
  startTime: string;
  endTime: string;
}

export interface Doctor {
  id: Guid;
  membershipId: Guid;
  displayNameAr: string;
  displayNameEn?: string | null;
  specialty: string;
  licenseNumber?: string | null;
  bio?: string | null;
  consultationFee: number;
  defaultSlotMinutes: number;
  acceptsOnlineBooking: boolean;
  isActive: boolean;
  schedule: ScheduleWindow[];
  rowVersion: string;
}

export interface SaveDoctorRequest {
  displayNameAr: string;
  displayNameEn?: string | null;
  specialty: string;
  licenseNumber?: string | null;
  bio?: string | null;
  consultationFee: number;
  defaultSlotMinutes: number;
  acceptsOnlineBooking: boolean;
}

export type AppointmentStatus = "Scheduled" | "Confirmed" | "CheckedIn" | "Completed" | "Cancelled" | "NoShow";
export type AppointmentSource = "Reception" | "Phone" | "Online" | "WalkIn";

export interface Appointment {
  id: Guid;
  branchId: Guid;
  patientId: Guid;
  patientName: string;
  patientMrn: string;
  patientMobile: string;
  doctorId: Guid;
  doctorName: string;
  serviceId?: Guid | null;
  serviceName?: string | null;
  startUtc: string;
  endUtc: string;
  status: AppointmentStatus;
  source: AppointmentSource;
  tokenNumber?: string | null;
  reason?: string | null;
  cancellationReason?: string | null;
  rowVersion: string;
  /** When the patient was checked in (absent before check-in) — the start of their waiting time. */
  checkedInAtUtc?: string | null;
}

export interface AvailableSlot {
  startUtc: string;
  endUtc: string;
  branchId: Guid;
  localTime: string;
}

export interface CreateAppointmentRequest {
  patientId: Guid;
  doctorId: Guid;
  branchId?: Guid | null;
  serviceId?: Guid | null;
  startUtc?: string | null;
  durationMinutes?: number | null;
  source: AppointmentSource;
  reason?: string | null;
}

// ------------------------------------------------------------------ visits & prescriptions

export type VisitStatus = "Open" | "Completed";

export interface VisitVitals {
  systolicBp?: number | null;
  diastolicBp?: number | null;
  heartRate?: number | null;
  respiratoryRate?: number | null;
  temperatureC?: number | null;
  oxygenSaturation?: number | null;
  weightKg?: number | null;
  heightCm?: number | null;
}

/** Clinical content: returned only with MedicalRecords.View, written only with MedicalRecords.Edit. */
export interface VisitClinicalData {
  symptoms?: string | null;
  diagnosis?: string | null;
  diagnosisCode?: string | null;
  clinicalNotes?: string | null;
  treatmentPlan?: string | null;
  vitals?: VisitVitals | null;
}

export interface Visit {
  id: Guid;
  branchId: Guid;
  patientId: Guid;
  patientName: string;
  patientMrn: string;
  doctorId: Guid;
  doctorName: string;
  appointmentId?: Guid | null;
  visitedAtUtc: string;
  followUpDate?: string | null;
  followUpNotes?: string | null;
  clinical?: VisitClinicalData | null;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
  rowVersion: string;
  status: VisitStatus;
  completedAtUtc?: string | null;
}

export interface VisitListItem {
  id: Guid;
  branchId: Guid;
  patientId: Guid;
  patientName: string;
  patientMrn: string;
  doctorId: Guid;
  doctorName: string;
  appointmentId?: Guid | null;
  visitedAtUtc: string;
  followUpDate?: string | null;
  /** Absent without MedicalRecords.View. */
  diagnosis?: string | null;
  status: VisitStatus;
  completedAtUtc?: string | null;
}

export interface CreateVisitRequest {
  patientId: Guid;
  doctorId: Guid;
  branchId?: Guid | null;
  appointmentId?: Guid | null;
}

export interface UpdateVisitRequest {
  clinical?: VisitClinicalData | null;
  followUpDate?: string | null;
  followUpNotes?: string | null;
  rowVersion: string;
}

export interface PrescriptionItem {
  lineNumber: number;
  medicationName: string;
  strength?: string | null;
  dosage?: string | null;
  frequency?: string | null;
  route?: string | null;
  durationDays?: number | null;
  instructions?: string | null;
}

export interface Prescription {
  id: Guid;
  number: string;
  patientId: Guid;
  patientName: string;
  patientMrn: string;
  doctorId: Guid;
  doctorName: string;
  visitId?: Guid | null;
  issuedAtUtc: string;
  notes?: string | null;
  items: PrescriptionItem[];
  createdAtUtc: string;
}

export interface PrescriptionListItem {
  id: Guid;
  number: string;
  patientId: Guid;
  doctorId: Guid;
  doctorName: string;
  visitId?: Guid | null;
  issuedAtUtc: string;
  itemCount: number;
}

/** The prescriber, patient and branch come from the visit and the signed-in doctor on the server. */
export interface CreatePrescriptionRequest {
  visitId: Guid;
  notes?: string | null;
  items: Omit<PrescriptionItem, "lineNumber">[];
}

// ------------------------------------------------------------------ saas

export interface UsageItem {
  limitKey: string;
  used: number;
  /** null = unlimited */
  max?: number | null;
}

export interface SubscriptionView {
  planCode: string;
  planNameAr: string;
  planNameEn: string;
  status: "Trialing" | "Active" | "PastDue" | "Canceled" | "Expired";
  billingCycle: "Monthly" | "Yearly";
  currentPeriodEndUtc: string;
  trialEndsAtUtc?: string | null;
  cancelAtPeriodEnd: boolean;
  features: Record<string, boolean>;
  usage: UsageItem[];
  /** False once the period has ended: the clinic is read-only until the platform extends it. */
  hasFullAccess: boolean;
}

export interface PublicPlan {
  code: string;
  nameAr: string;
  nameEn: string;
  descriptionAr?: string | null;
  descriptionEn?: string | null;
  monthlyPrice: number;
  yearlyPrice: number;
  currency: string;
  trialDays: number;
  features: string[];
  /** limitKey -> max (null = unlimited) */
  limits: Record<string, number | null>;
}
