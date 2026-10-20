/** Permission names (backend: Domain/Authorization/Permissions.cs). The UI hides what the user can't do; the API enforces. */
export const P = {
  Patients: { View: "Patients.View", Create: "Patients.Create", Edit: "Patients.Edit", Delete: "Patients.Delete" },
  MedicalRecords: { View: "MedicalRecords.View", Edit: "MedicalRecords.Edit" },
  Doctors: { View: "Doctors.View", Manage: "Doctors.Manage" },
  Appointments: {
    View: "Appointments.View",
    Create: "Appointments.Create",
    Edit: "Appointments.Edit",
    Cancel: "Appointments.Cancel",
    CheckIn: "Appointments.CheckIn",
  },
  Visits: { View: "Visits.View", Create: "Visits.Create", Edit: "Visits.Edit" },
  Prescriptions: { View: "Prescriptions.View", Create: "Prescriptions.Create" },
  Staff: { View: "Staff.View", Manage: "Staff.Manage" },
  Roles: { Manage: "Roles.Manage" },
  Branches: { Manage: "Branches.Manage" },
  Settings: { Manage: "Settings.Manage" },
  Reports: { View: "Reports.View" },
  Subscription: { View: "Subscription.View", Manage: "Subscription.Manage" },
  AuditLogs: { View: "AuditLogs.View" },
} as const;
