# مخطط قاعدة البيانات (SQL Server)

> المصدر الحقيقي للمخطط هو الـMigrations في `backend/src/ClinicSaaS.Infrastructure/Persistence/Migrations`.
> هذا الملف يشرح **لماذا** صُمم المخطط هكذا. السكربت الكامل المولّد: [001_InitialCoreSchema.sql](001_InitialCoreSchema.sql).

## 1. القواعد العامة

| القاعدة | التطبيق |
|---|---|
| المفاتيح | `uniqueidentifier` تُولّد تسلسلياً من EF (مناسبة للـclustered index في SQL Server). `AuditLogs` تستخدم `bigint IDENTITY` لأنها append-only وكثيفة الكتابة |
| العزل | كل جدول تابع لعيادة يحتوي `TenantId NOT NULL` + FK إلى `Tenants` |
| العلاقات بين جداول العيادة | **FK مركّب `(TenantId, X)` → `(TenantId, Id)`**: قاعدة البيانات نفسها ترفض أن يشير موعد في عيادة A إلى مريض في عيادة B، حتى عبر SQL خام |
| النصوص | لا يوجد `nvarchar(max)` — الحد الافتراضي 256 وكل عمود له طول مبرر. `varchar` للقيم ASCII (أرقام جوال، رموز، hashes) |
| المبالغ | `decimal(18,2)` — لا `float` |
| الوقت | `datetime2(3)` بتوقيت UTC للحظات الزمنية؛ `date` لتاريخ الميلاد؛ `time(0)` لساعات العمل (بتوقيت العيادة المحلي) |
| الـEnums | تُخزن نصوصاً `nvarchar(32)` لتكون مقروءة في SQL وفي الـfiltered indexes ولا تتأثر بإعادة ترتيب القيم |
| التزامن | `rowversion` على كل جدول قابل للتعديل من أكثر من مستخدم |
| الحذف | Soft delete (`IsDeleted`, `DeletedAtUtc`, `DeletedByUserId`) للبيانات الطبية والمالية؛ لا يوجد `ON DELETE CASCADE` من `Tenants` |
| الفهارس | كل فهرس تشغيلي يبدأ بـ`TenantId` (يخدم فلتر العزل ويسمح بالتقسيم لاحقاً) |

## 2. الجداول المنفذة (Migration 001)

```text
Platform                         Identity / RBAC                       Clinical
────────                         ───────────────                       ────────
Plans ──< PlanFeatures           Users ──< TenantMemberships >── Tenants   Patients
      ──< PlanLimits                   ──< RefreshTokens       │           DoctorProfiles ──< DoctorSchedules
Tenants ──< Branches                   ──< VerificationCodes   │           ClinicServices
        ──< Subscriptions >── Plans    TenantMemberships ──< MembershipRoles >── Roles ──< RolePermissions
        ──< TenantFeatureOverrides                         ──< MembershipBranches >── Branches
        ──< NumberSequences                                                Appointments >── Patients, DoctorProfiles,
AuditLogs (append-only)                                                                    Branches, ClinicServices
```

### القيود المهمة

| القيد | السبب |
|---|---|
| `UX_Appointments_ActiveSlot` UNIQUE `(TenantId, DoctorId, StartUtc)` WHERE Status IN (Scheduled, Confirmed, CheckedIn) AND Source <> 'WalkIn' | الحاجز النهائي ضد حجزين متزامنين لنفس الموعد. المواعيد الملغاة لا تحجز الوقت، والـwalk-in مسموح بتداخله |
| `IX_Appointments_Doctor_Start` `(TenantId, DoctorId, StartUtc)` INCLUDE (EndUtc, Status, Source) | استعلام التداخل `start < newEnd AND end > newStart` بدون Key lookup |
| `CK_Appointments_Range` EndUtc > StartUtc | لا مواعيد بمدة صفرية أو سالبة |
| `UX_Patients_Tenant_Identity` UNIQUE `(TenantId, IdentityType, IdentityNumber)` WHERE IdentityNumber IS NOT NULL AND IsDeleted = 0 | منع تكرار المريض بنفس الهوية داخل العيادة (الجوال المشترك بين أفراد العائلة مسموح) |
| `UX (TenantId, MedicalRecordNumber)` | رقم الملف فريد لكل عيادة |
| `UX_Subscriptions_Tenant_Current` UNIQUE `(TenantId)` WHERE Status IN (Trialing, Active, PastDue) | اشتراك فعّال واحد فقط لكل عيادة |
| `NumberSequences (TenantId, Name)` + `UPDATE … OUTPUT` داخل الـtransaction | ترقيم بلا تكرار وبلا فجوات (مطلوب لأرقام الفواتير الضريبية) |
| `UX Users.NormalizedEmail` | هوية واحدة لكل بريد على مستوى المنصة |
| `UX (TenantId, UserId)` في TenantMemberships | عضوية واحدة لكل مستخدم في كل عيادة |
| `UX RefreshTokens.TokenHash`, `UX VerificationCodes.CodeHash` | البحث بالـhash (القيمة الأصلية لا تُخزن أبداً) |
| Check constraints على الأسعار، المدد، أيام الأسبوع، الحدود | منع البيانات غير المنطقية من أي مصدر |

## 2.1 السجلات السريرية (Migration 003 — `ClinicalRecords`، وحالة الزيارة في 004 — `VisitCompletion`)

```text
Patients ──< Visits >── DoctorProfiles, Branches, Appointments (اختياري)
Patients ──< Prescriptions >── DoctorProfiles, Visits (اختياري)
             Prescriptions ──< PrescriptionItems
Patients ──< FileAttachments >── Visits (اختياري)
```

| الجدول | الأعمدة الرئيسية | ملاحظات التصميم |
|---|---|---|
| `Visits` | BranchId، PatientId، DoctorId، AppointmentId (nullable)، VisitedAtUtc، الأعراض، التشخيص، `DiagnosisCode` (ICD-10)، الملاحظات السريرية، خطة العلاج، 8 أعمدة للعلامات الحيوية، FollowUpDate/Notes، **Status** + **CompletedAtUtc** + **CompletedByUserId** (Migration 004) | FK مركّب لكل مرجع؛ soft delete؛ rowversion. المحتوى السريري يتطلب `MedicalRecords.View` ولا يُكتب في السجلات. دورة الحياة أدناه |
| `Prescriptions` | Number (`RX-000001` من `NumberSequences`)، PatientId، DoctorId، VisitId (nullable)، IssuedAtUtc، Notes | وثيقة لا تُعدّل بعد الإصدار (التصحيح = وصفة جديدة)؛ soft delete؛ rowversion |
| `PrescriptionItems` | PrescriptionId، LineNumber، اسم الدواء، التركيز، الجرعة، التكرار، طريقة الاستخدام، المدة (1–365 يوماً)، التعليمات | FK مركّب إلى `Prescriptions` بـ`CASCADE` (البنود جزء من الوصفة؛ الوصفات نفسها تُحذف حذفاً ناعماً فقط) |
| `FileAttachments` | **PatientId** (إلزامي)، **VisitId** (اختياري)، FileName، ContentType، SizeBytes، StorageKey، Sha256، Category، **ScanStatus** | انظر أدناه |

**لماذا `PatientId/VisitId` وليس `OwnerType/OwnerId`:** الربط العام (polymorphic) لا يمكن أن يكون FK، فلن تمنع قاعدة البيانات
ملفاً من الإشارة إلى مريض عيادة أخرى. الأعمدة الصريحة تسمح بـFK مركّب `(TenantId, PatientId)` و`(TenantId, VisitId)`.
أي مالك جديد للملفات لاحقاً يُضاف كعمود FK صريح بـMigration موثق.

**`ScanStatus` (فحص البرمجيات الخبيثة):**

```text
Pending ──► Scanning ──► Clean      (نهائية — الحالة الوحيدة المسموح بتنزيلها)
   │            ├──────► Rejected   (نهائية — لا يُعاد قبول الملف أبداً)
   └──► Failed ◄┘
        └──► Pending   (إعادة المحاولة)
```

- كل ملف جديد يبدأ `Pending` ولا يُنزَّل حتى يصبح `Clean` (`FileAttachment.CanDownload`، ويتطلب أيضاً `IsDeleted = 0`).
- `CK_FileAttachments_ScanStatus` يرفض أي قيمة خارج الحالات الخمس على مستوى قاعدة البيانات.
- الانتقالات تُفرض في الـDomain (`TransitionScanTo`)؛ خدمة الفحص نفسها تُضاف مع endpoints الملفات (المرحلة 6).

**`Visits.Status` (Migration 004 — `VisitCompletion`):**

```text
Open ──► Completed   (نهائية — تقفل تعديل المحتوى السريري)
```

- كل زيارة جديدة (وكل زيارة سابقة لـ004 عبر `DEFAULT 'Open'`) تبدأ `Open`. لا يوجد انتقال عكسي.
- الانتقال الوحيد عبر `Visit.Complete(nowUtc, userId)` (أي محاولة أخرى ← `INVALID_STATUS_TRANSITION`)، ويختم `CompletedAtUtc` و`CompletedByUserId`.
- `Visit.CanEditClinicalContent` = `Open` وغير محذوفة؛ `Visit.IsCompleted` = `Completed`. فرض القفل في الخدمات/الـendpoints مسؤولية طبقة Application (T-013b).
- `CompletedByUserId` عمود تدقيق بلا FK، مثل `DeletedByUserId`؛ قد يكون NULL مع `Completed` (فاعل نظامي).

**القيود والفهارس:**

| القيد | السبب |
|---|---|
| `AK_Appointments_TenantId_Id` **(تغيير مقصود على جدول قائم)** | مفتاح بديل `(TenantId, Id)` على `Appointments` ليدعم الـFK المركّب `(TenantId, AppointmentId)` من `Visits`، مثل باقي الجداول السريرية. `Id` مفتاح أساسي أصلاً فلا يرفض أي صف قائم |
| `UX_Visits_Appointment` UNIQUE `(TenantId, AppointmentId)` WHERE AppointmentId IS NOT NULL AND IsDeleted = 0 | زيارة واحدة فعّالة لكل موعد |
| `IX_Prescriptions_TenantId_Number` UNIQUE | رقم الوصفة فريد لكل عيادة |
| `IX_PrescriptionItems_TenantId_PrescriptionId_LineNumber` UNIQUE | ترتيب البنود ثابت وبلا تكرار |
| `IX_FileAttachments_StorageKey` UNIQUE | مفتاح التخزين عالمي (يولده الخادم) — الفهرس الوحيد الذي لا يبدأ بـ`TenantId` لأن التفرد مطلوب على مستوى المنصة |
| `CK_Visits_Bp`, `CK_Visits_Spo2`, `CK_PrescriptionItems_Duration`, `CK_FileAttachments_Size`, `CK_FileAttachments_ScanStatus` | قيم منطقية فقط من أي مصدر |
| `CK_Visits_Status` (004) | `[Status] IN ('Open','Completed')` — قفل التعديل يثق بهذا العمود |
| `CK_Visits_Completion` (004) | `Completed` ⇔ `CompletedAtUtc IS NOT NULL`؛ أي حالة غير `Completed` بلا `CompletedAtUtc`/`CompletedByUserId` — لا صف "نصف مكتمل" |
| `IX_Prescriptions_TenantId_DoctorId` | فهرس FK أنشأه EF تلقائياً (لا يوجد فهرس آخر يبدأ بـ`TenantId, DoctorId`) |

`Diagnoses` (جدول مرجعي لـICD-10) لم يُنشأ: عمود `Visits.DiagnosisCode` يكفي حالياً، والجدول المرجعي يُضاف عند الحاجة.

## 2.2 التحقق بخطوتين لحسابات المنصة (Migration 005 — `PlatformAdminMfa`)

```text
Users ──< UserTotpFactors      (0..1 لكل مستخدم — فهرس فريد على UserId)
Users ──< UserRecoveryCodes    (10 عند كل تفعيل/إعادة توليد)
```

| الجدول | الأعمدة | ملاحظات التصميم |
|---|---|---|
| `UserTotpFactors` | `SecretProtected` varbinary(256) (سر TOTP مشفّر بـData Protection، مربوط بالمستخدم)، `LastUsedTimeStep` bigint NULL (آخر خطوة 30 ثانية مقبولة)، `CreatedAtUtc` | الصف لا يُنشأ إلا بعد إثبات امتلاك التطبيق؛ الإزالة = حذف الصف. مملوك للمنصة (بلا `TenantId`). `ON DELETE CASCADE` من `Users` |
| `UserRecoveryCodes` | `CodeHash` char(64) (SHA-256 لرمز بقوة 80 بت)، `CreatedAtUtc`، `ConsumedAtUtc` NULL | الاستهلاك `UPDATE … WHERE ConsumedAtUtc IS NULL` واحد ذري. فهرس فريد `(UserId, CodeHash)`. `ON DELETE CASCADE` من `Users` |

لا يوجد سر بنص صريح في أي مكان؛ مفاتيح التشفير خارج قاعدة البيانات (`docs/operations/platform-admin-mfa.md`).

## 3. الجداول المخطط لها (تُضاف في مراحلها بـMigration موثق)

| المرحلة | الجداول | ملاحظات التصميم |
|---|---|---|
| 6 | `Diagnoses` (اختياري: رموز ICD-10) | جدول مرجعي عام + ربط بالزيارة |
| 6 | `DoctorTimeOff` | إجازات/استثناءات من جدول العمل |
| 8 | `Invoices` (رقم تسلسلي من NumberSequences، الحالة، العملة، Subtotal/Discount/Taxable/VAT/Total، snapshot للرقم الضريبي وبيانات العميل، UUID و hash سابق للفوترة الإلكترونية) | `decimal(18,2)`؛ الأرقام مشتقة على الخادم فقط |
| 8 | `InvoiceLines` (الوصف، الكمية، السعر، الخصم، نسبة VAT، مبلغ VAT، الإجمالي) | نسبة الضريبة لكل بند (مثلاً المواطن السعودي في الخدمات الصحية الخاصة) |
| 8 | `Payments`, `Refunds`, `CreditNotes` | الاسترداد عبر إشعار دائن وليس تعديل الفاتورة |
| 8 | `PlatformInvoices`, `PlatformPayments`, `PaymentGatewayEvents` (idempotency key)، `Coupons`, `CouponRedemptions` | فوترة المنصة للعيادة منفصلة عن فوترة العيادة للمريض |
| 9 | `NotificationMessages` (Outbox: Channel، Recipient، TemplateKey، Status، Attempts، NextAttemptAt)، `NotificationTemplates`, `InAppNotifications` | Outbox يُكتب في نفس الـtransaction |
| 9 | `UsageCounters` (TenantId، LimitKey، Period، Count) | عدادات SMS/WhatsApp الشهرية |
| 50 | `PublicPageSettings` | الشعار، الوصف، الألوان حسب الباقة |

## 4. الأمان على مستوى قاعدة البيانات

- مستخدم التطبيق بصلاحيات `SELECT/INSERT/UPDATE/DELETE/EXECUTE` فقط، ومستخدم منفصل للـMigrations (DDL) — انظر [security/least-privilege.sql](security/least-privilege.sql).
- كل الاستعلامات عبر EF Core (parameterized). الـSQL الخام الوحيد (`SequenceGenerator`) يستخدم `FormattableString` فيُرسل كـparameters.
- التشفير: TDE أو تشفير التخزين الذي يوفّره المزوّد في الإنتاج (مهمة الخطة 3I؛ يتبع اختيار الاستضافة)، و Always Encrypted مرشح لرقم الهوية إن تطلبت المراجعة النظامية ذلك.
