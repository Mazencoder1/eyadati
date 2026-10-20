# سجل تغييرات المخطط (Schema Changelog)

كل تغيير في قاعدة البيانات يُوثق هنا قبل تطبيقه: المخطط الحالي → تحليل الأثر → المبرر → خطة الـMigration → الاختبارات → التطبيق.

---

## 001 — `InitialCoreSchema` (2026-10-06)

| البند | التفاصيل |
|---|---|
| **المخطط الحالي** | لا يوجد (النظام السابق على MongoDB، لا ترحيل بيانات — انظر `docs/03-migration-plan.md`) |
| **التغيير** | إنشاء 22 جدولاً: المنصة (Tenants, Branches, Plans, PlanFeatures, PlanLimits, Subscriptions, TenantFeatureOverrides, NumberSequences)، الهوية (Users, TenantMemberships, MembershipRoles, MembershipBranches, Roles, RolePermissions, RefreshTokens, VerificationCodes)، السريري الأساسي (Patients, DoctorProfiles, DoctorSchedules, ClinicServices, Appointments)، والتدقيق (AuditLogs) |
| **المبرر** | المرحلتان 2 و3: عزل المستأجرين، RBAC بصلاحيات، اشتراكات وخطط كبيانات، ترقيم بلا فجوات، وأساس المرضى/الأطباء/المواعيد |
| **تحليل الأثر** | قاعدة جديدة — لا أثر على بيانات قائمة. بيانات أولية: 4 خطط (الأسعار placeholder بانتظار القرار التجاري) |
| **خطة التطبيق** | `dotnet ef database update` في التطوير؛ في البيئات الأخرى عبر سكربت idempotent مُراجع (`dotnet ef migrations script --idempotent`) يُطبّقه مستخدم الـMigrations |
| **التراجع** | `dotnet ef database update 0` (قاعدة جديدة فقط) |
| **الاختبارات** | 31 اختبار تكامل على SQL Server حقيقي (LocalDB) تشمل: رفض الـFK المركّب لمرجع من عيادة أخرى عبر SQL خام، الفهرس الفريد للهوية، `rowversion`، الترقيم المتزامن، الحذف الناعم |
| **الحالة** | ✅ مُطبق على `ClinicSaaS_Dev` |

---

## 002 — `AppointmentBoardIndexAndDurationGuard` (2026-10-06)

| البند | التفاصيل |
|---|---|
| **المخطط الحالي** | `Appointments` بفهارس `(TenantId, DoctorId, StartUtc)`, `(TenantId, PatientId, StartUtc)`, `(TenantId, BranchId, StartUtc)` |
| **التغيير** | 1) فهرس `IX_Appointments_Tenant_Start (TenantId, StartUtc) INCLUDE (Status, Source)` · 2) قيد `CK_Appointments_MaxDuration`: مدة الموعد ≤ 480 دقيقة |
| **المبرر** | 1) لوحة مواعيد اليوم لكل الأطباء والفروع (بدون فلتر طبيب أو فرع) كانت ستمسح كل مواعيد العيادة. 2) استعلام منع التداخل يحدّ بحث الفهرس بشرط `StartUtc >= البداية − 480 دقيقة`؛ القيد يضمن صحة هذا الافتراض على مستوى قاعدة البيانات وليس في الكود فقط |
| **تحليل الأثر** | فهرس إضافي على جدول المواعيد (تكلفة كتابة صغيرة). القيد لا يؤثر على بيانات قائمة (الـvalidation يرفض أصلاً المدد > 480) |
| **خطة التطبيق** | سكربت idempotent: [002_AppointmentBoardIndexAndDurationGuard.sql](002_AppointmentBoardIndexAndDurationGuard.sql). على الجداول الكبيرة في الإنتاج يُنشأ الفهرس بـ`ONLINE = ON` في نافذة صيانة |
| **التراجع** | `dotnet ef database update InitialCoreSchema` |
| **الاختبارات** | اختبارات المواعيد: التداخل، الحواف المتلامسة، سباق 8 حجوزات متزامنة، اللوحة اليومية |
| **الحالة** | ✅ مُطبق على `ClinicSaaS_Dev` |

---

## 003 — `ClinicalRecords` (2026-10-06)

| البند | التفاصيل |
|---|---|
| **المخطط الحالي** | 001 + 002. كيانات `Visit`/`Prescription`/`PrescriptionItem`/`FileAttachment` كانت موجودة في الـmodel بدون migration، فكان `MigrateAsync` يفشل بـ`PendingModelChangesWarning` (65/65 اختبار تكامل فشلت) |
| **التغيير** | 1) إنشاء 4 جداول جديدة: `Visits`, `Prescriptions`, `PrescriptionItems`, `FileAttachments` (التفاصيل في [schema.md §2.1](schema.md)). 2) **تغيير مقصود ومعتمد على جدول قائم:** `AK_Appointments_TenantId_Id` UNIQUE `(TenantId, Id)` على `Appointments` |
| **المبرر** | 1) أساس المرحلة 6 (الزيارات، الوصفات، المرفقات). 2) الـFK المركّب `(TenantId, AppointmentId)` من `Visits` يحتاج مفتاحاً بديلاً `(TenantId, Id)` على `Appointments` — نفس النمط المطبق على Patients/DoctorProfiles/Branches/ClinicServices — حتى ترفض قاعدة البيانات زيارة تشير إلى موعد عيادة أخرى |
| **قرارات التصميم** | `FileAttachments` بأعمدة `PatientId`/`VisitId` صريحة بدل `OwnerType/OwnerId` (الربط العام لا يقبل FK). إضافة `ScanStatus` (Pending/Scanning/Clean/Rejected/Failed) مع `CK_FileAttachments_ScanStatus`؛ التنزيل مسموح فقط عند `Clean` |
| **تحليل الأثر** | **إضافي فقط:** لا حذف أو تعديل أو إعادة تسمية لأي جدول/عمود قائم، ولا تغيير بيانات. `AK_Appointments_TenantId_Id` لا يمكن أن يفشل على بيانات قائمة (`Id` مفتاح أساسي)؛ تكلفته فهرس إضافي على `Appointments` |
| **خطة التطبيق** | سكربت idempotent: [003_ClinicalRecords.sql](003_ClinicalRecords.sql). على جدول `Appointments` كبير في الإنتاج: إنشاء الـconstraint في نافذة صيانة. **لا يُطبّق على الإنتاج قبل مراجعة واعتماد منفصلين** |
| **التراجع** | `dotnet ef database update AppointmentBoardIndexAndDurationGuard` — يحذف الجداول الأربعة الجديدة وبياناتها والـconstraint (آمن فقط قبل وجود بيانات سريرية حقيقية) |
| **الاختبارات** | عزل المستأجرين للزيارات/الوصفات/الملفات/المواعيد (فلتر القراءة بالـId، حاجز الحفظ، كيانات مزوّرة، FK مركّب عبر SQL خام، `CK_FileAttachments_ScanStatus`)؛ آلة حالات `ScanStatus` (unit). التحقق: Up → Down إلى 002 → Up على قاعدة اختبار منفصلة |
| **ملاحظة** | `TenantId` أصبح concurrency token لكل الكيانات (إصلاح أمني — انظر `docs/04-security-model.md`). تغيير في الـmodel فقط: migration تجريبية بعده خرجت **فارغة** (لا تغيير في المخطط) |
| **الحالة** | ✅ مُطبق ومُختبر على قواعد اختبار مؤقتة فقط (`ClinicSaaS_Test_<guid>`). **لم يُطبق على Development أو Production** |

---

## 004 — `VisitCompletion` (2026-10-06)

| البند | التفاصيل |
|---|---|
| **المخطط الحالي** | 001 + 002 + 003. جدول `Visits` لا يحتوي أي حالة: لا يمكن تخزين أن الزيارة أُغلقت، فلا يمكن قفل تعديل المحتوى السريري بعد إنهائها |
| **التغيير** | على جدول `Visits` فقط، **إضافي**: 1) عمود `Status` nvarchar(32) NOT NULL DEFAULT `'Open'` (enum `VisitStatus { Open, Completed }` مخزّن نصاً كباقي الـenums). 2) عمود `CompletedAtUtc` datetime2(3) NULL. 3) عمود `CompletedByUserId` uniqueidentifier NULL. 4) قيد `CK_Visits_Status`: `[Status] IN ('Open','Completed')`. 5) قيد `CK_Visits_Completion`: `Completed` ⇔ `CompletedAtUtc IS NOT NULL`، وأي حالة غير `Completed` لا تحمل `CompletedAtUtc`/`CompletedByUserId`. لا فهارس جديدة |
| **المبرر** | إجراء "إنهاء الزيارة" الصريح (T-013b) يغلق الزيارة ويقفل تعديل المحتوى السريري ويُكمل الموعد المرتبط. الانتقال الوحيد `Open → Completed` يُفرض في الـDomain (`Visit.Complete(nowUtc, userId)` — أي انتقال آخر يرمي `INVALID_STATUS_TRANSITION`)، مع `Visit.IsCompleted` و`Visit.CanEditClinicalContent` (Open وغير محذوفة) |
| **قرارات التصميم** | `CK_Visits_Status` يتبع نمط `CK_FileAttachments_ScanStatus`: قفل التعديل يثق بهذا العمود، فترفض قاعدة البيانات أي قيمة أخرى من أي مصدر. `CK_Visits_Completion` يمنع صفاً "نصف مكتمل" (Completed بلا وقت، أو Open بأختام إكمال)؛ كُتب بصيغة `<> 'Completed'` حتى يفحص الاتساق فقط ويبقى نطاق القيم مسؤولية `CK_Visits_Status` (كل قيد يُختبر مستقلاً). `CompletedByUserId` يُسمح أن يكون NULL مع `Completed` (فاعل نظامي/غير معروف). **بدون FK** على `CompletedByUserId` — نفس عرف أعمدة التدقيق `*ByUserId` القائمة (`DeletedByUserId`, `CreatedByUserId`...) التي لا تملك FK. لا يوجد انتقال `Completed → Open` (إعادة الفتح تحتاج قراراً وتغييراً منفصلين) |
| **تحليل الأثر** | **إضافي فقط:** لا حذف أو إعادة تسمية لأي جدول/عمود، ولا إعادة كتابة بيانات سوى ملء القيمة الافتراضية: كل الزيارات القائمة تصبح `Open` عبر `DEFAULT 'Open'` (قيد default باسم يولّده SQL Server، كعمود `IsDeleted`). القيدان يُتحقق منهما على الصفوف القائمة عند الإضافة ولا يمكن أن يفشلا (كلها Open بأختام NULL). `ALTER TABLE ... ADD ... NOT NULL DEFAULT` عملية metadata-only في SQL Server 2012+ Enterprise/Azure SQL؛ في الإصدارات الأخرى قد تعيد كتابة الجدول — زمنها متناسب مع حجم `Visits` |
| **غير مشمول عمداً (اعتبار مُبلَّغ)** | **لا backfill من حالة الموعد:** الزيارات القائمة المرتبطة بموعد `Completed` تبقى `Open` (أي قابلة للتعديل) بعد الـmigration. تسجيل الزيارة اليوم يُكمل موعدها، فقد تكون كل الزيارات القائمة مرتبطة بمواعيد مكتملة؛ تحويلها إلى `Completed` قرار منتج (يقفلها فوراً بلا `CompletedByUserId`) ويحتاج اعتماداً وسكربت بيانات منفصلاً. حالياً لا توجد بيانات سريرية حقيقية (003 لم يُطبق خارج قواعد الاختبار) |
| **خطة التطبيق** | سكربت idempotent: [004_VisitCompletion.sql](004_VisitCompletion.sql) (فرق 004 فقط، من `ClinicalRecords` إلى `VisitCompletion`). يتطلب تطبيق 003 أولاً. **لا يُطبّق على Development أو Production قبل مراجعة واعتماد منفصلين** |
| **التراجع** | `dotnet ef migrations script VisitCompletion ClinicalRecords` (أو `database update ClinicalRecords` بيد بشرية): يحذف القيدين ثم الأعمدة الثلاثة (EF يحذف قيد الـdefault تلقائياً). **يفقد حالة الإكمال وأختامها**؛ بعد إعادة التطبيق تعود كل الزيارات `Open`. بقية بيانات الزيارة لا تتأثر |
| **الاختبارات** | Unit (`VisitCompletionTests`، 6): حالة البداية، `Open → Completed` مع الأختام، فاعل NULL، القفل بعد الإكمال، رفض الإكمال مرتين بـ`INVALID_STATUS_TRANSITION` مع بقاء الأختام الأصلية، الزيارة المحذوفة غير قابلة للتعديل. Integration (`VisitCompletionSchemaTests`، 6): `CK_Visits_Status` يرفض `'Closed'` عبر SQL خام، القيمة الافتراضية `Open` لصف مُدرج بلا `Status`، `CK_Visits_Completion` يرفض الحالتين نصف المكتملتين ويقبل Completed بلا مستخدم، حفظ `Complete()` واسترجاعه (UTC)، عيادة B لا تستطيع إكمال زيارة عيادة A حتى لو تجاوزت الفلتر (`TenantIsolationViolationException`)، و**Up(004) → Down(003) → Up(004)** عبر `IMigrator` على قاعدة `ClinicSaaS_Test_<guid>` منفصلة تُنشأ وتُحذف داخل الاختبار، مع زيارة سابقة لـ004 تصبح `Open` ويبقى باقي صفها بعد Down. اختبارات عزل الزيارات القائمة (003) بقيت ناجحة |
| **ملاحظة (snapshot)** | الـmodel snapshot التقط أيضاً `IsConcurrencyToken()` على `TenantId` لكل الكيانات (إصلاح 003 الأمني، تغيير model فقط لم يُسجَّل في الـsnapshot سابقاً). لا ينتج عنه أي عملية في Up/Down ولا أي SQL في السكربت |
| **الحالة** | ✅ مُطبق ومُختبر على قواعد اختبار مؤقتة فقط (`ClinicSaaS_Test_<guid>`). **لم يُطبق على Development أو Production** |

---

## 005 — `PlatformAdminMfa` (2026-10-08)

| البند | التفاصيل |
|---|---|
| **المخطط الحالي** | 001 + 002 + 003 + 004. لا يوجد أي مكان لسر مصادقة ثنائية أو حالة تسجيلها أو رموز استرداد: `Users` و`VerificationCodes` و`RefreshTokens` لا تصلح لذلك (موثق في `docs/PHASE3-PRODUCTION-PLAN.md` §19.5) |
| **التغيير** | **جدولان جديدان فقط**، دون أي تغيير في جدول قائم: 1) `UserTotpFactors` (`Id` PK، `UserId` FK→`Users` ON DELETE CASCADE، `SecretProtected` varbinary(256) NOT NULL — سر TOTP مشفّر بـASP.NET Core Data Protection ومربوط بالمستخدم، `LastUsedTimeStep` bigint NULL — حماية الإعادة، `CreatedAtUtc` datetime2(3) NOT NULL) + فهرس فريد على `UserId` (عامل واحد كحد أقصى لكل مستخدم). 2) `UserRecoveryCodes` (`Id` PK، `UserId` FK→`Users` ON DELETE CASCADE، `CodeHash` char(64) NOT NULL — SHA-256 لرمز عشوائي بقوة 80 بت، `CreatedAtUtc`، `ConsumedAtUtc` NULL) + فهرس فريد على `(UserId, CodeHash)` |
| **المبرر** | التحقق بخطوتين إلزامي لحسابات المنصة (`PlatformRole != None`) قبل الإنتاج (3A-D5 / P3-SEC-04): المشرف العام يستطيع تغيير حالة أي عيادة وباقتها واشتراكها. البريد الإلكتروني ليس عاملاً ثانياً لأنه قناة استعادة كلمة المرور نفسها |
| **قرارات التصميم** | لا صف "قيد التفعيل": العامل لا يُحفظ إلا بعد أن يثبت المستخدم امتلاك التطبيق (الحالة المعلّقة رمز مشفّر قصير العمر لا صف). لا `DisabledAtUtc`: الإزالة حذف للصف (السر لا يبقى)، والتاريخ في `AuditLogs`. CASCADE يطابق `RefreshTokens`/`VerificationCodes` (مواد اعتماد تتبع المستخدم). الجدولان مملوكان للمنصة (بلا `TenantId`، بلا فلتر عام، بلا FK مركّب). `datetime2(3)` هو اصطلاح المشروع للأوقات (الطلب الأصلي كتب `datetime2`) |
| **تحليل الأثر** | **إضافي فقط:** لا حذف أو تعديل أو إعادة تسمية لأي جدول/عمود/فهرس/قيد قائم، ولا تغيير بيانات أو بذور (يثبته اختبار يقارن بصمة كل ما عدا الجدولين قبل/بعد الـmigration). الأثر السلوكي في التطبيق: أي حساب منصة بلا عامل لا يملك جلسة صالحة (تُنهى الجلسات القديمة) ويتطلب تسجيل الدخول تفعيل المصادقة. حسابات العيادات لا تتأثر |
| **متطلب إنتاج** | مفاتيح Data Protection يجب أن تُحفظ خارج قاعدة البيانات ومحمية بشهادة خارجها، وتُنسخ احتياطياً منفصلة عن القاعدة — انظر `docs/operations/platform-admin-mfa.md`. بدونها لا يبدأ الـAPI في بيئة شبيهة بالإنتاج |
| **خطة التطبيق** | سكربت idempotent: [005_PlatformAdminMfa.sql](005_PlatformAdminMfa.sql) (فرق 005 فقط، من `VisitCompletion` إلى `PlatformAdminMfa`). يتطلب تطبيق 004 أولاً. **لا يُطبّق على الإنتاج إلا بمراجعة وموافقة** |
| **التراجع** | `dotnet ef database update VisitCompletion`: يحذف الجدولين (يفقد عوامل المصادقة المسجلة: يعيد حسابات المنصة إلى كلمة المرور فقط، أي أن الإصدار القديم من التطبيق هو المطلوب معه). آمن قبل أي تسجيل فعلي |
| **الاختبارات** | `PlatformMfaSchemaTests` (5): الأعمدة والأنواع بالضبط، PK/فهارس فريدة/FK مع CASCADE، رفض عاملين لنفس المستخدم ورمزين متطابقين ويتيم، الحذف المتتالي عند حذف المستخدم، Up→Down→Up على قاعدة مؤقتة مع مقارنة بصمة كل ما سواهما، لا تغييرات معلّقة في الـmodel. و`PlatformMfaTests` (40) للسلوك |
| **الحالة** | ✅ مُطبق ومُختبر على قواعد اختبار مؤقتة فقط (`ClinicSaaS_Test_<guid>`). **لم يُطبق على Development أو Production** |
