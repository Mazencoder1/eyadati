using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Api.Errors;

/// <summary>
/// Localized messages for error and validation codes (ar default, en). Clients receive the code *and* a message,
/// so they can show the message as-is or map the code themselves.
/// Phase 10 moves this catalog into resource files shared with the frontend.
/// </summary>
public static class ErrorCatalog
{
    private static readonly Dictionary<string, (string Ar, string En)> Messages = new()
    {
        [ErrorCodes.Validation] = ("يرجى تصحيح الحقول المظللة والمحاولة مرة أخرى.", "Please correct the highlighted fields and try again."),
        [ErrorCodes.NotFound] = ("العنصر المطلوب غير موجود.", "The requested item was not found."),
        [ErrorCodes.Forbidden] = ("ليست لديك صلاحية لتنفيذ هذا الإجراء.", "You don't have permission to do that."),
        [ErrorCodes.Unauthorized] = ("يرجى تسجيل الدخول للمتابعة.", "Please sign in to continue."),
        [ErrorCodes.Conflict] = ("تعارض مع بيانات موجودة.", "This conflicts with existing data."),
        [ErrorCodes.ConcurrencyConflict] = ("قام مستخدم آخر بتعديل هذه البيانات. حدّث الصفحة وأعد المحاولة.", "Someone else changed this record. Refresh and try again."),
        [ErrorCodes.RateLimited] = ("طلبات كثيرة. يرجى الانتظار قليلاً ثم المحاولة.", "Too many requests. Please wait and try again."),
        [ErrorCodes.TemporarilyUnavailable] = ("الخدمة غير متاحة مؤقتاً. تحقق مما إذا تمت العملية الأخيرة ثم حاول مجدداً بعد لحظات.", "The service is temporarily unavailable. Check whether your last action went through, then try again in a moment."),
        [ErrorCodes.Internal] = ("حدث خطأ أثناء تنفيذ العملية.", "Something went wrong while processing your request."),

        [ErrorCodes.InvalidCredentials] = ("البريد الإلكتروني أو كلمة المرور غير صحيحة.", "Incorrect email or password."),
        [ErrorCodes.AccountLocked] = ("تم قفل الحساب مؤقتاً بسبب محاولات دخول فاشلة. حاول لاحقاً.", "Account temporarily locked after failed attempts. Try again later."),
        [ErrorCodes.AccountDisabled] = ("هذا الحساب موقوف. تواصل مع إدارة العيادة.", "This account is disabled. Contact your clinic administrator."),
        [ErrorCodes.EmailNotVerified] = ("يرجى تأكيد بريدك الإلكتروني أولاً.", "Please verify your email first."),
        [ErrorCodes.InvalidRefreshToken] = ("انتهت الجلسة. يرجى تسجيل الدخول مجدداً.", "Your session has ended. Please sign in again."),
        [ErrorCodes.ClientTypeNotAllowed] = ("هذا النوع من العملاء غير مسموح به من المتصفح.", "This client type is not allowed from a browser."),
        [ErrorCodes.InvalidOrExpiredToken] = ("الرابط غير صالح أو منتهي الصلاحية.", "This link is invalid or has expired."),
        [ErrorCodes.EmailTaken] = ("يوجد حساب مسجل بهذا البريد الإلكتروني.", "An account with this email already exists."),
        [ErrorCodes.TenantSelectionRequired] = ("اختر العيادة التي تريد الدخول إليها.", "Choose which clinic to sign in to."),
        [ErrorCodes.NoActiveMembership] = ("لا يوجد لديك وصول فعّال لأي عيادة.", "You don't have active access to a clinic."),
        [ErrorCodes.TenantSuspended] = ("حساب العيادة موقوف. تواصل مع الدعم.", "This clinic's account is suspended. Contact support."),
        [ErrorCodes.SubscriptionExpired] = ("انتهى اشتراك العيادة. يمكنك الاطلاع على البيانات فقط حتى يُجدَّد الاشتراك.", "The clinic's subscription has ended. Data is read-only until the subscription is renewed."),
        [ErrorCodes.PlanNotFound] = ("الباقة غير موجودة أو غير متاحة.", "Plan not found or not available."),
        [ErrorCodes.TenantRequired] = ("يجب اختيار عيادة لتنفيذ هذا الإجراء.", "A clinic context is required."),

        [ErrorCodes.UsageLimitReached] = ("وصلت إلى الحد المسموح في باقتك. قم بترقية الباقة للمتابعة.", "You've reached your plan limit. Upgrade your plan to continue."),
        [ErrorCodes.FeatureNotAvailable] = ("هذه الميزة غير متاحة في باقتك الحالية.", "This feature isn't available on your current plan."),

        [ErrorCodes.PatientNotFound] = ("المريض غير موجود.", "Patient not found."),
        [ErrorCodes.DuplicatePatientIdentity] = ("يوجد مريض مسجل بنفس رقم الهوية.", "A patient with this ID number already exists."),
        [ErrorCodes.AppointmentNotFound] = ("الموعد غير موجود.", "Appointment not found."),
        [ErrorCodes.InvalidStatusTransition] = ("لا يمكن تغيير حالة الموعد بهذا الشكل.", "That status change isn't allowed."),
        [ErrorCodes.CancellationReasonRequired] = ("سبب الإلغاء مطلوب.", "A cancellation reason is required."),
        [ErrorCodes.SlotTaken] = ("هذا الموعد محجوز. اختر وقتاً آخر.", "That slot is taken. Pick another time."),
        [ErrorCodes.OutsideWorkingHours] = ("الوقت المختار خارج ساعات عمل الطبيب.", "The selected time is outside the doctor's working hours."),
        [ErrorCodes.SlotInPast] = ("لا يمكن الحجز في وقت مضى.", "You can't book a time in the past."),
        [ErrorCodes.AppointmentNotEditable] = ("لا يمكن تعديل هذا الموعد في حالته الحالية.", "This appointment can't be changed in its current status."),
        [ErrorCodes.NoShowBeforeStart] = ("لا يمكن تسجيل عدم الحضور قبل وقت الموعد.", "You can't mark a no-show before the appointment time."),
        [ErrorCodes.DoctorNotFound] = ("الطبيب غير موجود.", "Doctor not found."),
        [ErrorCodes.DoctorInactive] = ("الطبيب غير نشط حالياً.", "This doctor is currently inactive."),
        [ErrorCodes.DoctorProfileExists] = ("هذا الموظف لديه ملف طبيب بالفعل.", "This staff member already has a doctor profile."),
        [ErrorCodes.ScheduleOverlap] = ("فترات العمل متداخلة في نفس اليوم.", "Working hours overlap on the same day."),
        [ErrorCodes.ServiceNotFound] = ("الخدمة غير موجودة.", "Service not found."),
        [ErrorCodes.VisitNotFound] = ("الزيارة غير موجودة.", "Visit not found."),
        [ErrorCodes.VisitAlreadyRecorded] = ("تم تسجيل زيارة لهذا الموعد بالفعل.", "A visit has already been recorded for this appointment."),
        [ErrorCodes.VisitCompleted] = ("تم إكمال هذه الزيارة ولا يمكن تعديلها.", "This visit has been completed and can no longer be edited."),
        [ErrorCodes.AppointmentNotCheckedIn] = ("سجّل حضور المريض أولاً قبل تسجيل الزيارة.", "Check the patient in before recording the visit."),
        [ErrorCodes.PrescriptionNotFound] = ("الوصفة غير موجودة.", "Prescription not found."),
        [ErrorCodes.PrescriberNotTreatingDoctor] = ("يصدر الوصفة الطبيب المعالج لهذه الزيارة فقط.", "Only the treating doctor of this visit can issue a prescription."),
        [ErrorCodes.VisitNotTreatingDoctor] = ("يوثّق الزيارة ويُنهيها الطبيب المعالج لها فقط.", "Only the treating doctor of this visit can record, edit or complete it."),
        [ErrorCodes.PatientHasHistory] = ("لا يمكن حذف مريض لديه زيارات أو وصفات أو مواعيد مسجلة.", "A patient with visits, prescriptions or appointments can't be deleted."),
        [ErrorCodes.FileNotFound] = ("الملف غير موجود.", "File not found."),
        [ErrorCodes.FileNotAvailable] = ("الملف غير متاح للتنزيل حتى يكتمل فحصه الأمني.", "This file can't be downloaded until it has passed the security scan."),
        [ErrorCodes.FileStorageUnavailable] = ("تعذر الوصول إلى مخزن الملفات. حاول لاحقاً.", "File storage is unavailable. Please try again later."),
        [ErrorCodes.PayloadTooLarge] = ("حجم الطلب أكبر من المسموح (10 ميجابايت للملف).", "The request is too large (10 MB per file)."),
        [ErrorCodes.BranchNotFound] = ("الفرع غير موجود.", "Branch not found."),
        [ErrorCodes.BranchRequired] = ("يرجى اختيار الفرع.", "Please choose a branch."),
        [ErrorCodes.BranchAccessDenied] = ("ليست لديك صلاحية على هذا الفرع.", "You don't have access to this branch."),
        [ErrorCodes.MainBranchProtected] = ("لا يمكن إيقاف الفرع الرئيسي.", "The main branch can't be deactivated."),
        [ErrorCodes.RoleNotFound] = ("الدور غير موجود.", "Role not found."),
        [ErrorCodes.RoleInUse] = ("لا يمكن حذف دور مُسند لموظفين.", "A role assigned to staff can't be deleted."),
        [ErrorCodes.RoleNameTaken] = ("يوجد دور بنفس الاسم.", "A role with this name already exists."),
        [ErrorCodes.SystemRoleProtected] = ("هذا الدور أساسي ولا يمكن تعديله أو حذفه.", "This built-in role can't be changed or deleted."),
        [ErrorCodes.PermissionEscalation] = ("لا يمكنك منح صلاحيات لا تملكها.", "You can't grant permissions you don't have."),
        [ErrorCodes.StaffNotFound] = ("الموظف غير موجود.", "Staff member not found."),
        [ErrorCodes.StaffAlreadyMember] = ("هذا الشخص عضو في فريق العيادة بالفعل.", "This person is already on the clinic team."),
        [ErrorCodes.InvitationSignInRequired] = ("لديك حساب بهذا البريد. سجّل الدخول بحسابك ثم اقبل الدعوة.", "You already have an account with this email. Sign in to it, then accept the invitation."),
        [ErrorCodes.OwnerProtected] =("لا يمكن تعديل حساب مالك العيادة.", "The clinic owner's seat can't be changed."),
        [ErrorCodes.CannotChangeSelf] = ("لا يمكنك تعديل صلاحيات حسابك بنفسك.", "You can't change your own access."),
        [ErrorCodes.TenantNotFound] = ("العيادة غير موجودة.", "Clinic not found."),
        [ErrorCodes.SessionNotFound] = ("الجلسة غير موجودة.", "Session not found."),
        [ErrorCodes.MfaRequired] = ("أدخل رمز التحقق من تطبيق المصادقة لإكمال الدخول.", "Enter the code from your authenticator app to finish signing in."),
        [ErrorCodes.MfaEnrollmentRequired] = ("يجب تفعيل التحقق بخطوتين لحسابك قبل المتابعة.", "You must set up two-step verification for your account before continuing."),
        [ErrorCodes.InvalidMfaCode] = ("رمز التحقق غير صحيح أو مستخدم من قبل.", "The verification code is incorrect or was already used."),
        [ErrorCodes.InvalidMfaToken] = ("انتهت صلاحية خطوة التحقق. سجّل الدخول من جديد.", "This verification step has expired. Sign in again."),
        [ErrorCodes.MfaAlreadyEnrolled] = ("التحقق بخطوتين مفعّل بالفعل لهذا الحساب.", "Two-step verification is already set up for this account."),

        // validation codes
        [ValidationCodes.Required] = ("هذا الحقل مطلوب.", "This field is required."),
        [ValidationCodes.TooLong] = ("النص أطول من المسموح.", "This value is too long."),
        [ValidationCodes.TooShort] = ("النص أقصر من المسموح.", "This value is too short."),
        [ValidationCodes.InvalidEmail] = ("البريد الإلكتروني غير صحيح.", "Enter a valid email address."),
        [ValidationCodes.InvalidSaudiMobile] = ("رقم الجوال غير صحيح. مثال: 05XXXXXXXX", "Enter a valid Saudi mobile, e.g. 05XXXXXXXX."),
        [ValidationCodes.InvalidNationalId] = ("رقم الهوية/الإقامة غير صحيح.", "Enter a valid national ID / Iqama number."),
        [ValidationCodes.InvalidVatNumber] = ("الرقم الضريبي غير صحيح (15 رقماً يبدأ وينتهي بـ 3).", "Enter a valid VAT number (15 digits, starts and ends with 3)."),
        [ValidationCodes.InvalidDate] = ("التاريخ غير صحيح.", "Enter a valid date."),
        [ValidationCodes.OutOfRange] = ("القيمة خارج النطاق المسموح.", "This value is out of range."),
        [ValidationCodes.InvalidValue] = ("القيمة غير صحيحة.", "This value is invalid."),
        [ValidationCodes.WeakPassword] = ("كلمة المرور يجب أن تكون 10 أحرف على الأقل وتحتوي على حروف وأرقام.", "Password must be at least 10 characters with letters and digits."),
        [ValidationCodes.InvalidSlug] = ("المعرّف يقبل الحروف الإنجليزية الصغيرة والأرقام والشرطة فقط.", "Use lowercase letters, digits and dashes only."),
        [ValidationCodes.FileEmpty] = ("الملف فارغ.", "The file is empty."),
        [ValidationCodes.FileTooLarge] = ("حجم الملف أكبر من المسموح (10 ميجابايت).", "The file is larger than the allowed size (10 MB)."),
        [ValidationCodes.FileTypeNotAllowed] = ("نوع الملف غير مسموح. المسموح: PDF و JPG و PNG.", "File type not allowed. Allowed: PDF, JPG, PNG."),
        ["slug_taken"] = ("هذا المعرّف مستخدم. اختر معرّفاً آخر.", "This handle is taken. Choose another."),
    };

    public static string Message(string code, string language)
    {
        if (!Messages.TryGetValue(code, out var m)) m = Messages[ErrorCodes.Internal];
        return language == "en" ? m.En : m.Ar;
    }

    public static bool Has(string code) => Messages.ContainsKey(code);
}
