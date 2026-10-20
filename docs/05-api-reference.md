# مرجع الـAPI (v1)

> القاعدة: `/api/v1` · JSON · `Authorization: Bearer <access token>` · `Accept-Language: ar|en` (ar افتراضياً).
> مستند OpenAPI الكامل في بيئة التطوير: `GET /openapi/v1.json`.

## صيغة الاستجابة

```jsonc
// نجاح
{ "success": true, "data": { … } }
// قائمة مُرقّمة
{ "success": true, "data": [ … ], "meta": { "page": 1, "pageSize": 20, "totalCount": 57, "totalPages": 3 } }
// خطأ
{ "success": false, "code": "SLOT_TAKEN", "message": "هذا الموعد محجوز. اختر وقتاً آخر.", "correlationId": "…",
  "errors": { "mobileNumber": ["رقم الجوال غير صحيح…"] }, "details": { … } }
```

| HTTP | المعنى |
|---|---|
| 400 | `VALIDATION_FAILED` مع `errors` لكل حقل |
| 401 | غير مسجل / token منتهٍ / **جلسته انتهت** (`UNAUTHORIZED`: خروج، تغيير كلمة المرور، إنهاء الجلسة، إيقاف العيادة…) → استدعِ `/auth/refresh`؛ إن فشل فالجلسة انتهت فعلاً ويلزم تسجيل دخول |
| 403 | لا صلاحية، أو `USAGE_LIMIT_REACHED` / `FEATURE_NOT_AVAILABLE` (اعرض "ترقية الباقة")، أو `CLIENT_TYPE_NOT_ALLOWED` (متصفح طلب وضع الجوال) |
| 404 | غير موجود — **ويشمل موارد العيادات الأخرى** |
| 409 | تعارض: `SLOT_TAKEN`، `CONCURRENCY_CONFLICT` (أعد التحميل)، تكرار |
| 422 | قاعدة عمل: `OUTSIDE_WORKING_HOURS`، `INVALID_STATUS_TRANSITION`… |
| 423 | `ACCOUNT_LOCKED` |
| 429 | `RATE_LIMITED` + `Retry-After` |
| 503 | `TEMPORARILY_UNAVAILABLE` + `Retry-After: 5` (المرحلة 3B، **إضافة غير كاسرة**): قاعدة البيانات أو قفل الحجز غير متاح لحظياً، أو حوض الاتصالات ممتلئ. لا تفاصيل داخلية في الجسم. القراءات البسيطة وفتح الاتصال أُعيدت محاولتهما تلقائياً؛ **الكتابات لا تُعاد تلقائياً أبداً**، فقد تكون آخر عملية نُفِّذت أو لم تُنفَّذ — تحقّق ثم أعد المحاولة. `GET /health/ready` يرد `503` بنص `Unhealthy` فقط |

القوائم تقبل `page`, `pageSize` (≤100), `search`, `sort` (مثل `-createdAt`؛ الحقول المسموحة فقط). التعديلات تتطلب `rowVersion` الذي أُعيد آخر مرة.

## Auth — `/auth`

| Method | المسار | الصلاحية | ملاحظات |
|---|---|---|---|
| POST | `/register` | عام | ينشئ العيادة + المالك ويُرجع جلسة |
| POST | `/login` | عام | `tenantId` اختياري؛ 409 `TENANT_SELECTION_REQUIRED` مع قائمة العيادات. **مستخدم المنصة** (`platformRole` ≠ `None`) بدون `tenantId` يحصل دائماً على **جلسة منصة** (`tenant: null`) حتى لو كان عضواً في عيادة؛ لدخول عيادة يرسل `tenantId` أو يستخدم `/switch-tenant` (T-008) **التحقق بخطوتين (حسابات المنصة فقط):** كلمة المرور وحدها لا تُنشئ جلسة ولا access token ولا refresh token ولا cookie — يرد 409 `MFA_REQUIRED` مع `details.challengeToken` (يوجد عامل، صلاحيته 5 دقائق) أو 409 `MFA_ENROLLMENT_REQUIRED` مع `details.enrollmentToken` (10 دقائق)، حتى لو طُلب `tenantId`. حسابات العيادات بلا تغيير. |
| POST | `/refresh` | عام (cookie) | تدوير الـrefresh token. الويب: من الـcookie فقط ولا يُرجع refresh token في الـbody أبداً. الجوال (`X-Client-Type: mobile`، بلا `Origin`/`Sec-Fetch-*`): من الـbody وتُرجَع النسخة الجديدة في الـbody. متصفح يرسل `X-Client-Type: mobile` → 403 `CLIENT_TYPE_NOT_ALLOWED` دون أي أثر (المرحلة 3A). طلب متصفح بـ`Origin` خارج `Cors:AllowedOrigins` → 403 `FORBIDDEN` (CSRF، المرحلة 3B؛ الطلبات بلا `Origin` لا تتأثر) |
| POST | `/logout` | عام | نفس قاعدة النقل (cookie للويب، body للجوال). ينهي الجلسة وكل access token صادر لها فوراً |
| POST | `/forgot-password` · `/reset-password` | عام | الرمز لمرة واحدة يُرسَل في **جسم** POST فقط (لا يُقبل من الـquery). روابط البريد (إعادة التعيين، التأكيد، الدعوة) تحمل الرمز في الـfragment: `…/reset-password#token=…` فلا يصل لأي خادم أو سجل وصول (المرحلة 3B). فشل إرسال البريد لا يُظهر خطأً للطالب ويُسجَّل فقط |
| POST | `/verify-email` · `/resend-verification` | عام · مسجل | |
| POST | `/mfa/enroll/start` | عام (رمز التفعيل) | `{ enrollmentToken }` من 409 `MFA_ENROLLMENT_REQUIRED`. يُرجع **مرة واحدة** `{ secret, setupUri, pendingToken, expiresAtUtc }` مع `Cache-Control: no-store`. 409 `MFA_ALREADY_ENROLLED` إن وُجد عامل |
| POST | `/mfa/enroll/complete` | عام (الرمز المعلّق) | `{ pendingToken, code }`. عند الصحة فقط: يُحفظ العامل (السر مشفّر) و10 رموز استرداد، وتُنهى كل الجلسات السابقة، ويُرجع `{ session, recoveryCodes }` (تُعرض مرة واحدة). رمز خاطئ → 401 `INVALID_MFA_CODE` ولا يُحفظ شيء |
| POST | `/mfa/verify` | عام (رمز التحدي) | `{ challengeToken, totpCode }` **أو** `{ challengeToken, recoveryCode }` (واحد فقط، وإلا 400). يُنشئ الجلسة (والـcookie للويب). خطأ موحّد 401 `INVALID_MFA_CODE` للرمز الخاطئ أو المُعاد أو المستهلك؛ 423 `ACCOUNT_LOCKED` بعد 5 محاولات (60 دقيقة) |
| POST | `/mfa/recovery-codes/regenerate` | مسجل (منصة) | `{ totpCode }` — يلزم TOTP حالي (رمز الاسترداد لا يكفي). يستبدل الرموز العشرة كلها ويُرجعها مرة واحدة؛ لا يوقف التحقق بخطوتين |
| POST | `/mfa/remove` | مسجل (منصة) | `{ currentPassword, totpCode }` أو `{ currentPassword, recoveryCode }`. يحذف العامل والرموز ويُنهي **كل** الجلسات (هذه أيضاً) دون إنشاء جلسة جديدة؛ تسجيل الدخول التالي يتطلب التفعيل من جديد |
| POST | `/accept-invitation` | عام (أو مسجل) | `{ token, newPassword? }` — انظر "قبول الدعوة" أدناه (T-010) |
| POST | `/change-password` | مسجل | يُنهي كل الجلسات ويعيد جلسة جديدة |
| GET | `/me` | مسجل | من الـtoken، مع `membershipId` (مقعد المستخدم في العيادة الحالية) و`doctorId` (ملف الطبيب لهذا المقعد، أو غائب). كلاهما يُشتق من الجلسة على الخادم ولا يُقبل من العميل (Phase 2، إضافة غير كاسرة) |
| GET | `/tenants` · POST `/switch-tenant` | مسجل | العيادات المتاحة والتبديل بينها |
| GET | `/sessions` · DELETE `/sessions/{id}` | مسجل | الأجهزة المسجلة وإنهاؤها |

## العيادة

| Method | المسار | الصلاحية |
|---|---|---|
| GET / PUT | `/settings` | عضو / `Settings.Manage` |
| GET / POST / PUT | `/branches`, `/branches/{id}` | عضو / `Branches.Manage` (+ حد `max_branches` وميزة `multi_branch`) |
| GET | `/permissions` | `Roles.Manage` |
| GET / POST / PUT / DELETE | `/roles`, `/roles/{id}` | `Staff.View` / `Roles.Manage` |
| GET / POST / PUT | `/staff`, `/staff/{id}` | `Staff.View` / `Staff.Manage` (+ حد `max_users`) |
| POST | `/staff/{id}/disable` · `/enable` · `/resend-invitation` | `Staff.Manage` |
| GET | `/subscription` | `Subscription.View` |
| GET | `/audit-logs` | `AuditLogs.View` (ليس ميزة باقة — متاح في كل الباقات، D13) |

**الدعوات والموافقة (T-010 / QA-02) — تغيير في العقد:**

- `POST /staff` ينشئ دائماً مقعداً **معلّقاً** `status: "Invited"` ويرسل رابط دعوة لمرة واحدة (7 أيام) إلى البريد، سواء كان البريد مسجلاً
  أم لا. لا يُضاف حساب موجود إلى العيادة مباشرة بعد الآن.
- الاستجابة **متطابقة** للبريد المسجل وغير المسجل (نفس الحالة 201 والحقول والقيم): ما دام المقعد `Invited` تُعاد القيم المقنّعة
  `fullName` = `email` = البريد المدعو بأحرف صغيرة، `userId` = `00000000-0000-0000-0000-000000000000`، `phoneNumber` و`lastLoginAtUtc`
  غائبان (null)، `emailConfirmed` = `hasPassword` = `false`. نفس التقنيع في `GET /staff` و`GET /staff/{id}`، والبحث `search` يطابق
  المقعد المعلّق بالبريد فقط (لا بالاسم). بعد القبول تظهر بيانات العضو الفعلية.
- **QA-12 (T-016):** المقعد الموقوف `Disabled` يبقى بنفس التقنيع (في `GET /staff/{id}` و`GET /staff` والبحث والترتيب) ما لم يكن
  الحساب قد انضم فعلاً إلى **هذه** العيادة سابقاً (قَبِل دعوتها، أو دخل/انتقل إليها بعضوية فعّالة — مُستدَل عليه من سجل التدقيق لهذه
  العيادة فقط). أي أن إيقاف دعوة لم تُقبل لا يكشف بيانات الحساب. العضو السابق الحقيقي يظهر ببياناته كما كان.
- المقعد المعلّق ليس عضوية فعّالة: لا يظهر في `/auth/tenants` ولا في `TENANT_SELECTION_REQUIRED`، والدخول/التبديل إليه →
  `NO_ACTIVE_MEMBERSHIP`. يُحتسب ضمن حد `max_users`.
- `POST /staff/{id}/disable` على مقعد معلّق = إلغاء الدعوة (الرابط يصبح غير صالح). `POST /staff/{id}/enable` على مقعد موقوف يعيده
  **معلّقاً** ويرسل دعوة جديدة (إعادة الربط تتطلب موافقة صاحب الحساب مجدداً — حتى لعضو سبق أن قبل)، وعلى مقعد معلّق لا يغيّر شيئاً.
- `POST /staff/{id}/resend-invitation` (202) يعمل على المقعد المعلّق فقط.

**قبول الدعوة `POST /auth/accept-invitation` `{ token, newPassword? }`** — الرابط مرتبط بالمقعد الذي صدر له:

| الحالة | الطلب | النتيجة |
|---|---|---|
| حساب جديد (بلا كلمة مرور) | بدون مصادقة، `newPassword` مطلوب | 204: تعيين كلمة المرور، تأكيد البريد، تفعيل المقعد. بدون `newPassword` → 400 على `newPassword` |
| حساب موجود | `Authorization: Bearer` لنفس الحساب (أي جلسة: عيادة أو منصة)؛ `newPassword` يُتجاهل | 204: تفعيل المقعد وتأكيد البريد. **كلمة المرور لا تتغير أبداً عبر دعوة** |
| حساب موجود | بدون مصادقة أو بحساب آخر | 409 `INVITATION_SIGN_IN_REQUIRED` (لا يتغير شيء) |
| رابط مستخدم/منتهٍ/ملغى، أو المقعد لم يعد معلّقاً | — | 400 `INVALID_OR_EXPIRED_TOKEN` |
| العيادة صاحبة المقعد موقوفة أو مغلقة (GD-04، المرحلة 3C) | أي طلب | 403 `TENANT_SUSPENDED` قبل أي تغيير: لا كلمة مرور تُعيَّن، الرابط لا يُستهلك والمقعد يبقى معلّقاً، فيعمل الرابط نفسه بعد إعادة التفعيل (ما لم تنتهِ صلاحيته). العيادة المنتهية فترتها (قراءة فقط) لا تتأثر |

بعد القبول يدخل المستخدم العيادة عبر `/auth/switch-tenant` أو `login` مع `tenantId`. المستهلكون في الواجهة:
`frontend/features/staff/staff-page.tsx` (عرض المقعد المعلّق، الإيقاف/التفعيل/إعادة الإرسال) و`frontend/features/auth/password-forms.tsx`
(صفحة قبول الدعوة: عند 409 `INVITATION_SIGN_IN_REQUIRED` توجيه لتسجيل الدخول ثم إعادة الطلب بنفس الـtoken مع المصادقة).

**قواعد إدارة الموظفين والفروع (T-009):**

- `branchIds` فارغ أو غائب = وصول لكل الفروع.
- المنفّذ **المقيّد بفروع** (يملك `Staff.Manage`): في `POST /staff` و`PUT /staff/{id}` يجب أن تكون `branchIds` غير فارغة وضمن فروعه
  فقط، وإلا 403 `BRANCH_ACCESS_DENIED`. ولا يدير (`PUT`، `disable`، `enable`) إلا عضواً كل فروعه الحالية ضمن فروعه (العضو غير المقيّد
  أو المقيّد بفرع آخر → 403 `BRANCH_ACCESS_DENIED`). المنفّذ غير المقيّد بلا تغيير.
- تغيير فروع عضو (إضافة/إزالة/رفع التقييد) يتطلب أن يملك المنفّذ كل صلاحيات أدوار العضو الحالية، مثل الإيقاف تماماً، وإلا 403
  `PERMISSION_ESCALATION`.

**الفروع للعضو المقيّد (T-011):** `GET /branches` يعرض كل فروع العيادة لكل عضو (الأسماء لازمة للعرض). العضو المقيّد الذي يملك
`Branches.Manage` يعدّل فروعه فقط (`PUT` لفرع آخر → 403 `BRANCH_ACCESS_DENIED`) ولا ينشئ فروعاً جديدة (`POST` → 403
`BRANCH_ACCESS_DENIED`، لأن الفرع الجديد خارج نطاقه).

## السريري

| Method | المسار | الصلاحية |
|---|---|---|
| GET / POST | `/patients` | `Patients.View` / `Patients.Create` (+ حد `max_patients`) |
| GET / PUT / DELETE | `/patients/{id}` | `Patients.View` / `Patients.Edit` / `Patients.Delete` |
| PUT | `/patients/{id}/medical` | `MedicalRecords.Edit` |
| GET | `/patients/duplicates?mobile=&identityNumber=` | `Patients.Create` |
| GET / POST / PUT | `/doctors`, `/doctors/{id}` | `Doctors.View` / `Doctors.Manage` (+ حد `max_doctors`) |
| PUT | `/doctors/{id}/schedule` | `Doctors.Manage` |
| GET | `/doctors/{id}/availability?date=YYYY-MM-DD&serviceId=&branchId=` | `Appointments.View` |
| GET / POST / PUT / DELETE | `/services`, `/services/{id}` | عضو / `Settings.Manage` |
| GET | `/appointments?date=YYYY-MM-DD&doctorId=&branchId=&patientId=&status=` | `Appointments.View` |
| POST | `/appointments` | `Appointments.Create` (+ حد `max_appointments_month`) |
| PUT | `/appointments/{id}/reschedule` | `Appointments.Edit` |
| POST | `/appointments/{id}/confirm` · `/no-show` | `Appointments.Edit` |
| POST | `/appointments/{id}/check-in` · `/complete` | `Appointments.CheckIn` |
| POST | `/appointments/{id}/cancel` (`reason` مطلوب) | `Appointments.Cancel` |

**`AppointmentDto`** يحوي `checkedInAtUtc` (وقت تسجيل الحضور، غائب قبله) لحساب زمن الانتظار في قائمة الاستقبال (Phase 2، إضافة غير كاسرة).

**حذف مريض (`DELETE /patients/{id}`، D6):** مسموح فقط لتصحيح تسجيل خاطئ. مريض له أي موعد (بأي حالة، حتى الملغى) أو زيارة أو وصفة أو
مرفق (حتى المحذوف منها) → 409 `PATIENT_HAS_HISTORY` ولا يتغير شيء. نموذج "إيقاف المريض" مؤجل (يحتاج تغييراً في المخطط).

**إنشاء موعد (`POST /appointments`):**

- `source` ≠ `WalkIn`: الحقل `startUtc` مطلوب، ويُتحقق من ساعات عمل الطبيب في الفرع (422 `OUTSIDE_WORKING_HOURS`)، ومن أن الوقت ليس في الماضي بأكثر من 5 دقائق (422 `SLOT_IN_PAST`)، ومن عدم التداخل مع موعد آخر للطبيب (409 `SLOT_TAKEN`).
- `source = WalkIn` (حضور بدون موعد): **الخادم يحدد وقت البداية (الآن)**. يجب أن يكون `startUtc` غائباً أو `null`؛ أي قيمة → 400 `VALIDATION_FAILED` مع خطأ على الحقل `startUtc` (رسالة رمز التحقق `invalid_value`). يحصل الموعد على رقم دور يومي `T-01`، `T-02`… ولا يخضع لفحص التداخل (من يحضر أولاً يُخدم أولاً).

**نطاق الفروع للأطباء وساعات العمل (T-011)** — للعضو المقيّد بفروع فقط؛ غير المقيّد بلا تغيير:

- يرى الطبيب إذا كان له نافذة عمل واحدة على الأقل في أحد فروعه، **أو لا جدول له إطلاقاً** (غير مرتبط بفرع بعد، ليتمكن مدير الفرع من
  إضافته وتحديد ساعاته). غير ذلك: مستبعد من `GET /doctors` و`GET /doctors/{id}` وكل الكتابات عليه → 404 `DOCTOR_NOT_FOUND`.
  نفس القاعدة عند تسجيل زيارة `POST /visits` (QA-16، T-016): طبيب مخفي عن العضو المقيّد → 404 `DOCTOR_NOT_FOUND`.
- `schedule` في كل الاستجابات يحوي نوافذ فروعه فقط؛ ساعات الفروع الأخرى لا تُعرض.
- `PUT /doctors/{id}/schedule`: كل النوافذ المرسلة يجب أن تكون في فروعه (وإلا 403 `BRANCH_ACCESS_DENIED` ولا يتغير شيء). الطلب
  **يستبدل نوافذ فروعه فقط ويحافظ على نوافذ الفروع الأخرى كما هي** (قائمة فارغة = حذف نوافذ فروعه فقط). فحص التداخل
  `SCHEDULE_OVERLAP` يشمل النوافذ المحفوظة غير المرئية له (الطبيب لا يكون في مكانين).
- `POST /doctors` بجدول يحوي فرعاً خارج فروعه → 403 `BRANCH_ACCESS_DENIED`.
- `PUT /doctors/{id}` (الملف الشخصي و`isActive`) يؤثر على كل فروع الطبيب، فيُسمح به فقط إذا كانت كل نوافذ الطبيب ضمن فروعه (أو لا جدول
  له)؛ طبيب مشترك مع فرع آخر → 403 `BRANCH_ACCESS_DENIED`.
- **المواعيد** مقيّدة بالفروع كما كانت. **المرضى** يبقون على مستوى العيادة (المحتوى الطبي محكوم بالصلاحيات). **الخدمات** على مستوى
  العيادة: `ClinicService` لا يحوي عمود فرع، فتوفر الخدمة حسب الفرع غير ممكن بدون تغيير في المخطط (غير منفّذ).

**التواريخ:** كل الأوقات `…Z` بتوقيت UTC. المعامل `date` يوم تقويمي ميلادي بتوقيت العيادة (Asia/Riyadh). الواجهة تعرض الوقت المحلي والهجري.

### الزيارات والوصفات (السجلات السريرية)

| Method | المسار | الصلاحية |
|---|---|---|
| GET | `/visits?patientId=&doctorId=&branchId=&appointmentId=&fromUtc=&toUtc=` (`sort`: `visitedAt`/`createdAt`، الافتراضي `-visitedAt`) | `Visits.View` |
| GET | `/patients/{id}/visits` | `Visits.View` |
| GET | `/visits/{id}` | `Visits.View` (+ `MedicalRecords.View` لكتلة `clinical`) |
| POST | `/visits` | `Visits.Create` (+ `MedicalRecords.Edit` إذا أُرسلت `clinical`) + **طبيب الزيارة نفسه** (GD-01) |
| PUT | `/visits/{id}` | `Visits.Edit` (+ `MedicalRecords.Edit` إذا أُرسلت `clinical`) + **طبيب الزيارة نفسه** (GD-01) |
| POST | `/visits/{id}/complete` `{ rowVersion }` | `Visits.Edit` + `MedicalRecords.Edit` (T-013b) + **طبيب الزيارة نفسه** (GD-01) |
| GET | `/patients/{id}/prescriptions` (`sort`: `issuedAt`/`number`، الافتراضي `-issuedAt`) | `Prescriptions.View` + `MedicalRecords.View` (T-012) |
| GET | `/prescriptions/{id}` | `Prescriptions.View` + `MedicalRecords.View` (T-012) |
| POST | `/prescriptions` | `Prescriptions.Create` (الزيارة مفتوحة فقط — مكتملة → 422 `VISIT_COMPLETED`) |
| GET | `/follow-ups?from=&to=&doctorId=&branchId=&booked=` | `Appointments.View` — انظر "المتابعات" أدناه |

**المتابعات `GET /follow-ups` (قرار قبول الـMVP، إضافة غير كاسرة):** قائمة المتابعات المستحقة ليحجزها الاستقبال. `from`/`to` تواريخ
بتوقيت العيادة (شاملة؛ الافتراضي اليوم … +7 أيام؛ `to` قبل `from` أو مدى أكثر من 92 يوماً → 400). `booked=true|false` اختياري.
كل عنصر: `patientId, patientName, patientMrn, followUpDate, doctorId, doctorName, branchId, booked, nextAppointmentUtc` — **لا
معرّف زيارة ولا أي محتوى سريري** (لا تشخيص ولا ملاحظات ولا أدوية ولا حساسية). `booked` = للمريض موعد (`Scheduled`/`Confirmed`/
`CheckedIn`/`Completed`) بعد تلك الزيارة. النطاق كالزيارات: العيادة الحالية فقط، فروع العضو المقيّد، والطبيب بلا `ViewAllDoctors`
يرى متابعات مرضاه فقط. المستهلك: صفحة "المتابعات" (حجز موعد مُعبأ مسبقاً بالمريض والطبيب والتاريخ).

```jsonc
// POST /visits
{ "patientId": "…", "doctorId": "…",
  "appointmentId": "…",        // اختياري
  "branchId": "…",             // اختياري: من الموعد إن وُجد، وإلا الفرع الوحيد المتاح
  "visitedAtUtc": "…Z",        // اختياري: الافتراضي وقت الخادم؛ لا يقبل المستقبل
  "clinical": {                // اختياري؛ يتطلب MedicalRecords.Edit
    "symptoms": "…", "diagnosis": "…", "diagnosisCode": "J02.9", "clinicalNotes": "…", "treatmentPlan": "…",
    "vitals": { "systolicBp": 120, "diastolicBp": 80, "heartRate": 88, "respiratoryRate": 16, "temperatureC": 38.2,
                "oxygenSaturation": 98, "weightKg": 72.5, "heightCm": 175 } },
  "followUpDate": "2026-10-20", "followUpNotes": "…" }
// PUT /visits/{id}
{ "visitedAtUtc": "…Z", "clinical": { … }, "followUpDate": "…", "followUpNotes": "…", "rowVersion": "…" }
// POST /prescriptions  (D8: الزيارة مطلوبة؛ المريض والطبيب والفرع تُشتق منها ومن المستخدم — أي patientId/doctorId/branchId في الطلب يُتجاهل)
{ "visitId": "…", "notes": "…",
  "items": [ { "medicationName": "Paracetamol", "strength": "500 mg", "dosage": "1 tablet", "frequency": "3 times daily",
               "route": "oral", "durationDays": 5, "instructions": "…" } ] }   // 1–30 بنداً
```

**قواعد الزيارة:**

- **المحتوى السريري** (الأعراض، التشخيص ورمزه، الملاحظات، خطة العلاج، العلامات الحيوية) في الكتلة `clinical`: تُعاد فقط لمن يملك
  `MedicalRecords.View` (وإلا تُحذف من الاستجابة، وفي القوائم يُحذف `diagnosis`)، وتُكتب فقط بصلاحية `MedicalRecords.Edit`
  (وإلا 403 `FORBIDDEN`)، مثل الكتلة الطبية للمريض. المتابعة (`followUpDate/Notes`) بيانات إدارية تظهر بـ`Visits.View`.
- في `PUT`: المريض والطبيب والفرع والموعد ثابتة بعد التسجيل. `clinical` غائبة/`null` = بلا تغيير (فلا يمسحها من لا يراها)، وإذا أُرسلت
  تستبدل الكتلة كاملة. حقلا المتابعة يُستبدلان دائماً. `visitedAtUtc` غائب = بلا تغيير. `rowVersion` قديم → 409 `CONCURRENCY_CONFLICT`.
- **الربط** (يتحقق منه الخادم): المريض/الطبيب/الفرع/الموعد من عيادة أخرى → 404 (`PATIENT_NOT_FOUND`، `DOCTOR_NOT_FOUND`،
  `BRANCH_NOT_FOUND`، `APPOINTMENT_NOT_FOUND`). الطبيب غير النشط → 422 `DOCTOR_INACTIVE`. الموعد يجب أن يخص نفس المريض والطبيب
  (وإلا 400 على `appointmentId`) ونفس الفرع (وإلا 400 على `branchId`)، وأن يكون `CheckedIn` أو `Completed`
  (وإلا 422 `APPOINTMENT_NOT_CHECKED_IN`). زيارة فعّالة واحدة لكل موعد → 409 `VISIT_ALREADY_RECORDED`. **تسجيل الزيارة أو تعديلها
  لا يغيّر حالة الموعد أبداً**؛ الإغلاق يتم فقط بإجراء "إكمال الزيارة" الصريح أدناه.
- **الحالة (T-013b، إضافة غير كاسرة):** `VisitDto` يحوي `status` (`Open` | `Completed`) و`completedAtUtc` و`completedByUserId`؛
  عناصر القوائم تحوي `status` و`completedAtUtc`. كل زيارة تبدأ `Open`.
- **إكمال الزيارة `POST /visits/{id}/complete` `{ "rowVersion": "…" }`** → 200 مع `VisitDto` المحدّث:
  - ينقل الزيارة `Open → Completed` ويسجّل الوقت والمستخدم. وإن كانت مرتبطة بموعد: `CheckedIn → Completed` حسب قواعد المواعيد؛
    الموعد `Completed` مسبقاً يبقى كما هو؛ أي حالة أخرى للموعد (مثلاً `Cancelled`) → 422 `INVALID_STATUS_TRANSITION` **ولا يتغير شيء**
    (معاملة واحدة، لا كتابة جزئية).
  - زيارة مكتملة مسبقاً → 422 `INVALID_STATUS_TRANSITION`. `rowVersion` قديم → 409 `CONCURRENCY_CONFLICT`. طلبات متزامنة على نفس
    الزيارة: ينجح واحد فقط (قفل `sp_getapplock` لكل زيارة + rowVersion).
  - نفس قواعد الرؤية لبقية كتابات الزيارة: زيارة عيادة أخرى أو فرع غير مسموح أو (للطبيب بلا `ViewAllDoctors`) طبيب آخر → 404
    `VISIT_NOT_FOUND`. لا يتطلب `Appointments.CheckIn` لإغلاق الموعد (انظر docs/04).
  - **القفل:** `PUT /visits/{id}` على زيارة مكتملة → 422 `VISIT_COMPLETED` (لا تعديل للمحتوى السريري ولا للمتابعة).
  - **الوصفات (قرار قبول الـMVP، QA-15 — تغيير سلوك):** لا تُصدر وصفة جديدة على زيارة مكتملة → 422 `VISIT_COMPLETED`. الوصفة تُكتب
    أثناء الزيارة المفتوحة؛ الفحص يتم تحت نفس قفل الإكمال لكل زيارة فلا يتداخل إصدار مع إكمال. الوصفات الصادرة قبل الإكمال تبقى
    مقروءة وقابلة للطباعة. لا تعديل ولا إلغاء للوصفات (D10).
  - سجل التدقيق: `visit.completed` (بلا محتوى سريري)، و`appointment.status_changed` (`from`/`to`/`via=visit_completed`) عند إغلاق الموعد.
  - المستهلك في الواجهة: صفحة الزيارة (زر "إكمال الزيارة"، عرض الحالة، تعطيل التعديل بعد الإكمال).
- **التأليف السريري (GD-01):** تسجيل الزيارة وتعديلها (السريري والإداري) وإكمالها للطبيب المعالج فقط، أي المستخدم الذي ملف الطبيب
  لمقعده هو `doctorId` الزيارة. غيره — مدير أو مالك ليس طبيب الزيارة، أو طبيب آخر يرى الزيارة بـ`ViewAllDoctors` — → 403
  `VISIT_NOT_TREATING_DOCTOR` (بعد قواعد الرؤية: غير المرئي يبقى 404). `MedicalRecords.ViewAllDoctors` صلاحية **قراءة** فقط. قواعد
  الوصفات (D8) دون تغيير.
- **سجلات الطبيب نفسه (T-012):** المستخدم الذي لمقعده ملف طبيب في هذه العيادة ولا يملك `MedicalRecords.ViewAllDoctors` يرى ويعدّل
  ويسجّل فقط الزيارات التي `doctorId` فيها هو ملفه: زيارات الأطباء الآخرين مستبعدة من القوائم و404 `VISIT_NOT_FOUND` في القراءة
  والتعديل (لا 403، لمنع كشف الوجود)، وتسجيل زيارة باسم طبيب آخر → 404 `DOCTOR_NOT_FOUND`. المستخدم بلا ملف طبيب (تمريض، استقبال،
  مدير…) تبقى عليه قواعد الصلاحيات كما هي. الصلاحية الجديدة `MedicalRecords.ViewAllDoctors` ضمن قالبي مالك العيادة ومدير العيادة، وليست
  ضمن قالب الطبيب.
- **الفروع:** العضو المقيّد بفروع يرى زيارات فروعه فقط (غيرها 404)، ولا يسجّل زيارة في فرع آخر (403 `BRANCH_ACCESS_DENIED`).
- أخطاء التحقق للحقول المتداخلة تأتي بمفاتيح مثل `clinical.Vitals.SystolicBp` و`items[0].MedicationName` (نفس نمط بقية الـAPI).

**قواعد الوصفة:** رقم لكل عيادة `RX-000001` من التسلسل؛ `issuedAtUtc` يحدده الخادم. **لا تعديل ولا حذف ولا إلغاء** (التصحيح = وصفة
جديدة؛ الإلغاء/الإبطال مع سبب ميزة مستقبلية بعد موافقة، D10)، ولذلك لا يُعاد `rowVersion`.

- **الإصدار (D8):** `visitId` **مطلوب** (غائب → 400 على `visitId`). الزيارة يجب أن تكون مرئية للمستخدم (عيادة أخرى، فرع غير مسموح،
  أو زيارة طبيب آخر لمن لا يملك `ViewAllDoctors` → 404 `VISIT_NOT_FOUND`). **المُصدِر هو المستخدم المسجل نفسه** ويجب أن يكون ملف
  الطبيب لمقعده هو طبيب الزيارة؛ غير ذلك (مدير أو مالك ليس طبيب الزيارة، أو طبيب آخر يرى الزيارة بـ`ViewAllDoctors`) → 403
  `PRESCRIBER_NOT_TREATING_DOCTOR` ولا يُستهلك رقم. المريض والطبيب يُؤخذان من الزيارة، وفرع الوصفة هو فرع الزيارة. طبيب الزيارة غير
  النشط → 422 `DOCTOR_INACTIVE`. يمكن الإصدار لزيارة مكتملة (QA-15).
- **القراءة** (القائمة والتفاصيل) تتطلب `MedicalRecords.View` إضافة إلى `Prescriptions.View` (وإلا 403). الوصفة المرتبطة بزيارة تتبع فرع
  الزيارة في تقييد الفروع؛ الوصفات القديمة غير المرتبطة بزيارة (قبل D8) تبقى مرئية على مستوى العيادة مثل المريض. الطبيب بلا
  `MedicalRecords.ViewAllDoctors` يرى وصفاته فقط (وصفة طبيب آخر → 404 `PRESCRIPTION_NOT_FOUND`).
- الأدوية لا تُكتب في سجل التدقيق (`prescription.issued`: الرقم وعدد البنود و`visitId` فقط).

### المرفقات (P1-02)

| Method | المسار | الصلاحية |
|---|---|---|
| POST | `/patients/{id}/attachments` (multipart/form-data: `file` مطلوب، `category`، `description`، `visitId`) | `Files.Upload` |
| GET | `/patients/{id}/attachments?visitId=` (`sort`: `createdAt`/`fileName`/`size`، الافتراضي `-createdAt`) | `Files.View` + `MedicalRecords.View` |
| GET | `/attachments/{id}` (البيانات الوصفية) | `Files.View` + `MedicalRecords.View` |
| GET | `/attachments/{id}/content` (الملف نفسه، دائماً `Content-Disposition: attachment`) | `Files.View` + `MedicalRecords.View` |

- **الأنواع المقبولة:** PDF و JPG/JPEG و PNG فقط، حتى **10 ميجابايت** للملف. النوع يُحدَّد من **توقيع الملف** (أول البايتات)، ويجب أن
  يتطابق معه الامتداد ونوع MIME المُرسل؛ أي اختلاف أو نوع آخر → 400 `VALIDATION_FAILED` على `file` (`file_type_not_allowed`). ملف فارغ →
  `file_empty`، أكبر من 10 ميجابايت → `file_too_large`، طلب أكبر من حد الطلب (10 ميجابايت + 64 كيلوبايت) → 413 `PAYLOAD_TOO_LARGE`.
  `category`: `LabResult`, `Imaging`, `Report`, `Prescription`, `Identity`, `Insurance`, `Other` (الافتراضي). `description` ≤ 500.
- **اسم الملف** للعرض واسم التنزيل فقط (يُنظَّف: آخر جزء من المسار، بلا محارف تحكم أو اتجاه، ≤ 255). التخزين بمفتاح عشوائي يولده الخادم؛
  الاستجابات لا تحوي مفتاح التخزين ولا مساراً على القرص.
- **الربط والعزل:** المريض والزيارة من العيادة الحالية فقط (غير ذلك 404 `PATIENT_NOT_FOUND` / `VISIT_NOT_FOUND`)، والزيارة يجب أن تخص
  نفس المريض (وإلا 400 على `visitId`). الملف المرتبط بزيارة يتبع رؤية تلك الزيارة: العضو المقيّد بفروع لا يرى ولا يربط ملفات زيارات
  الفروع الأخرى، والطبيب بلا `MedicalRecords.ViewAllDoctors` يرى ملفات زياراته فقط؛ غير المرئي → 404 `FILE_NOT_FOUND`. الملف غير
  المرتبط بزيارة على مستوى العيادة مثل المريض، **إلا للطبيب بلا `ViewAllDoctors`: يرى الملف غير المرتبط فقط إذا رفعه هو**
  (`CreatedByUserId`). نفس القاعدة للقائمة والبيانات الوصفية والتنزيل. يمكن إرفاق ملف بزيارة مكتملة (مثل نتيجة مختبر تصل لاحقاً).
- **القراءة** (القائمة، البيانات الوصفية، التنزيل) تتطلب `Files.View` **و** `MedicalRecords.View` معاً (وإلا 403)، مثل الوصفات والمحتوى
  السريري للزيارة. الرفع يتطلب `Files.Upload` فقط ولا يمنح القراءة.
- **الفئة (`category`) معلوماتية فقط**: يختارها الرافع ولا يُتحقق منها، ولا تدخل في أي قرار صلاحيات.
- **الفحص والحجر:** لا يوجد فاحص فيروسات بعد. كل ملف يبدأ `scanStatus: "Pending"` و`canDownload: false`، والتنزيل مسموح **فقط** عند
  `Clean`؛ أي حالة أخرى (`Pending`, `Scanning`, `Failed`, `Rejected`) → 422 `FILE_NOT_AVAILABLE` مع `details.scanStatus`. لا يوجد أي
  مسار في الكود يجعل الملف `Clean` — **التنزيل غير متاح عملياً حتى يُعتمد ويُنفَّذ فاحص**.
- **الأخطاء:** تعذر الوصول للتخزين (كتابة أو قراءة أو بايتات مفقودة) → 503 `FILE_STORAGE_UNAVAILABLE` بلا تفاصيل داخلية. إذا فشل حفظ
  البيانات الوصفية بعد تخزين البايتات تُحذف البايتات (لا ملفات يتيمة).
- **التخزين:** قرص محلي للتطوير والاختبارات فقط (`FileStorage:Local:Root`، خارج أي مجلد عام، ملفات `0600`)؛ الـAPI يرفض البدء في أي بيئة أخرى (Staging، Production…) بهذا
  المزود. سجل التدقيق: `file.uploaded` (المريض، `visitId` عند الربط، الفئة، النوع، الحجم)، `file.viewed` (البيانات الوصفية)، `file.listed`
(المريض، مرشح الزيارة، العدد) و`file.downloaded` — لا أسماء ملفات ولا أوصاف ولا مسارات. لا حذف عبر الـAPI (لا توجد صلاحية).

## المنصة (Super Admin)

| Method | المسار | الصلاحية |
|---|---|---|
| GET | `/platform/tenants?status=&search=` | `Platform.Tenants.View` |
| POST | `/platform/tenants/{id}/suspend` · `/reactivate` | `Platform.Tenants.Manage` |
| GET | `/platform/tenants/{id}/subscription` | `Platform.Billing.View` |
| PUT | `/platform/tenants/{id}/subscription` | `Platform.Billing.Manage` |

**الفوترة اليدوية (D1، Gate C):** `PUT /platform/tenants/{id}/subscription`
`{ "planCode": "professional", "status": "Active", "billingCycle": "Yearly", "currentPeriodEndUtc": "…Z", "rowVersion": "…" }`

- `status`: `Trialing` | `Active` | `PastDue` فقط (غيرها → 400). `Canceled`/`Expired` لا تُضبط يدوياً: الانتهاء يُشتق من نهاية الفترة،
  وإنهاء الوصول مبكراً يتم بالإيقاف (`suspend`).
- `currentPeriodEndUtc` يجب أن يكون في المستقبل وخلال 3 سنوات (وإلا 400). باقة غير موجودة أو غير نشطة → 404 `PLAN_NOT_FOUND`؛
  عيادة غير موجودة → 404 `TENANT_NOT_FOUND`. `rowVersion` (من `GET`) مطلوب عند وجود اشتراك حالي؛ قديم → 409 `CONCURRENCY_CONFLICT`.
- تبدأ فترة جديدة (`currentPeriodStartUtc` = الآن) عند تغيير الباقة أو نهاية الفترة. `Trialing` يضبط `trialEndsAtUtc` = نهاية الفترة.
- التخفيض لا يحذف ولا يخفي أي بيانات: حدود الباقة تمنع إضافة عناصر جديدة فقط. كل تغيير يُسجَّل `subscription.changed` (قبل/بعد).

**وصول العيادة (Gate C) — يُفحص في كل طلب بـtoken عيادة** (`TenantAccessMiddleware`):

| الحالة | السبب | النتيجة |
|---|---|---|
| `Full` | العيادة نشطة وفترة الاشتراك الحالية لم تنتهِ | بلا قيود |
| `ReadOnly` | انتهت الفترة (انتهاء التجربة أو عدم التجديد) | `GET`/`HEAD` تعمل؛ كل كتابة → 403 `SUBSCRIPTION_EXPIRED` حتى يمدد المشرف الفترة |
| `Suspended` | أوقف المشرف العيادة (`suspend`) | كل الطلبات → 403 `TENANT_SUSPENDED` **فوراً** (لا انتظار لانتهاء الـtoken) |

- مسارات `/auth/*` مستثناة دائماً من حالة العيادة والاشتراك (تسجيل الخروج، تبديل العيادة، تغيير كلمة المرور؛ الدخول والتجديد يرفضان العيادة الموقوفة بأنفسهما)، **عدا `GET /auth/me`** فهو مسار عيادة.
- `GET /auth/me` يعيد `access` (`Full` | `ReadOnly`) بنفس القاعدة، و`GET /subscription` يعيد `hasFullAccess`. **تغيير (المرحلة 3A):** لعيادة موقوفة يرد `/auth/me` الآن 403 `TENANT_SUSPENDED` كباقي مسارات العيادة (كان 200 مع `access = Suspended`)، لأن الإيقاف يُنهي الجلسات فلا توجد جلسة حيّة لتُخبَر؛ الواجهة تعرض شاشة "العيادة موقوفة" عند هذا الكود.
- العيادة الجديدة المسجلة ذاتياً تبدأ تجربة 14 يوماً على باقة `basic` (`Saas:DefaultPlanCode`).
- **العضوية (GD-02):** يُفحص أيضاً في كل طلب أن عضوية الـtoken (`mid`) ما زالت `Active` وتخص نفس العيادة والمستخدم؛ غير ذلك →
  401 `NO_ACTIVE_MEMBERSHIP` فوراً على نفس الـaccess token (كل مسارات العيادة و`GET /auth/me`). بقية مسارات `/auth/*` (الخروج، تبديل
  العيادة، قائمة العيادات) تبقى متاحة. التعطيل يلغي أيضاً الـrefresh tokens فينتهي التجديد. إعادة التفعيل تجعل المقعد دعوة معلّقة
  (QA-02) فيبقى الرفض حتى قبولها. جلسات المنصة (بلا عيادة في الـtoken) غير متأثرة بفحص العضوية.
- **الجلسة (GD-03 / GD-15، المرحلة 3A):** يُفحص في كل طلب موثّق، لتوكن العيادة **والمنصة**، أن `sid` ما زال جلسة حيّة (عائلة refresh
  فيها token غير ملغى وغير منتهٍ لنفس المستخدم ونفس العيادة)؛ غير ذلك → 401 `UNAUTHORIZED` فوراً. يسري ذلك على الخروج، تغيير/إعادة
  تعيين كلمة المرور، `DELETE /auth/sessions/{id}`، `switch-tenant` (الجلسة القديمة)، إيقاف العيادة، تعطيل العضو، وإعادة استخدام
  refresh قديم. **إعادة تفعيل عيادة موقوفة لا تُعيد الجلسات التي أنهاها الإيقاف.** ترتيب الرفض: العضوية (`NO_ACTIVE_MEMBERSHIP`) ثم
  حالة العيادة (`TENANT_SUSPENDED`) ثم الجلسة (`UNAUTHORIZED`) ثم القراءة فقط (`SUBSCRIPTION_EXPIRED`)، فيحتفظ الموقوف والمعطّل
  برمزه المحدد. على المسارات العامة تُعامل الجلسة المنتهية كطلب مجهول. تكلفة الفحص: استعلام واحد لكل طلب (مدمج مع فحص العضوية)؛
  طلب العيادة ما زال استعلامين قبل المعالج، وطلب المنصة صار استعلاماً واحداً بعد أن كان صفراً.

**فصل المستويين (T-008):** كل token يخص مستوى واحداً فقط:

- **جلسة منصة** (token بلا `tid`): صلاحيات `Platform.*` حسب `platformRole` فقط، ولا صلاحيات عيادة.
- **جلسة عيادة** (token فيه `tid`): صلاحيات أدوار العضوية في تلك العيادة فقط، **ولا تحمل أي `Platform.*`** حتى لو كان المستخدم
  Super Admin. أي endpoint تحت `/platform` يرد 403 `FORBIDDEN` على token عيادة (الخادم يرفض صلاحية منصة مع `tid` حتى لو وُجدت
  في الـtoken).
- `/auth/refresh` يحافظ على مستوى الجلسة. `/auth/switch-tenant` من جلسة منصة يُنهي جلسة المنصة ويُصدر جلسة عيادة بلا صلاحيات
  منصة. **للعودة إلى المنصة:** `POST /auth/login` بدون `tenantId`.

## عام

| Method | المسار |
|---|---|
| GET | `/plans` (مخزن مؤقتاً 5 دقائق). يعرض فقط المزايا الموجودة فعلاً (`multi_branch`) والحدود المطبقة (`max_users`, `max_doctors`, `max_patients`, `max_appointments_month`, `max_branches`)، ويستثني الباقات المذكورة في `Saas:CatalogHiddenPlanCodes` (الافتراضي `free`). بيانات الباقات نفسها لم تتغير (بلا migration) |
| GET | `/health/live`, `/health/ready` |
