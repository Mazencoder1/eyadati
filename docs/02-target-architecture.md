> Target architecture written before implementation. Parts are built (API, tenancy, auth, clinical core); parts are
> future scope (public booking page, billing, notifications, super-admin panel). What exists today: root
> [`README.md`](../README.md) and [`STATUS.md`](STATUS.md).

# البنية المستهدفة (Target Architecture)

## 1. نظرة عامة

```text
                 ┌──────────────────────────── Frontend (Next.js, RTL, i18n) ───────────────────────────┐
                 │ Marketing site │ Public clinic page + booking │ Tenant dashboard │ Super-admin panel │
                 └───────────────────────────────────────┬──────────────────────────────────────────────┘
                                                         │ HTTPS · JSON · JWT (access) + refresh cookie
┌────────────────────────────────────────── ASP.NET Core API (/api/v1) ─────────────────────────────────────────┐
│ Middleware: CorrelationId → Exception→unified error → RateLimiter → AuthN (JWT) → Tenant resolution → AuthZ  │
│ Controllers (thin) ──► Application services (use cases, validation, permissions, business rules)             │
│                        ──► Domain (entities, invariants, state machines, value objects)                       │
│                        ──► Infrastructure (EF Core DbContext, tenant filters, interceptors, providers)        │
└──────────────────────────────────────────────────────┬─────────────────────────────────────────────────────────┘
                                                       │
                 SQL Server (shared DB, TenantId column + Global Query Filters + composite indexes)
                 Background jobs (outbox) · Private file storage · Notification providers · Payment providers
```

## 2. طبقات الـBackend

| المشروع | المسؤولية | يعتمد على |
|---|---|---|
| `ClinicSaaS.Domain` | الكيانات، القواعد الثابتة (Invariants)، آلات الحالة، الصلاحيات، الأخطاء | لا شيء |
| `ClinicSaaS.Application` | Use cases، Validation، واجهات (`IAppDbContext`, `ICurrentUser`, `IClock`...)، Paging | Domain |
| `ClinicSaaS.Infrastructure` | EF Core، Migrations، JWT، Password hashing، Providers (SMS/Email/Payment/Storage) | Application |
| `ClinicSaaS.Api` | Controllers، Middleware، Rate limiting، Health checks، DI composition | Infrastructure |

مسار كل طلب (المتطلب §36):

```text
Authentication → Tenant (من الـtoken فقط) → Permission → Resource ownership (Query filter) → Business rules
```

## 3. Multi-Tenancy

- **نموذج البيانات:** قاعدة واحدة مشتركة + عمود `TenantId` في كل جدول تابع لمستأجر. يتوسع لـ10,000 عيادة مع
  Composite indexes تبدأ بـ`TenantId`، ويمكن لاحقاً نقل مستأجرين كبار إلى قاعدة مستقلة (Sharding بالـTenantId) دون تغيير الكود.
- **العزل في الـDatabase access layer:**
  1. `HasQueryFilter(e => e.TenantId == CurrentTenantId)` على كل كيان `ITenantOwned` — إن لم يوجد Tenant تُرجع الاستعلامات صفراً (deny-by-default).
  2. `TenantSaveChangesInterceptor`: يفرض `TenantId` عند الإضافة، ويرفض أي تعديل/حذف لسجل من Tenant آخر، ويمنع تغيير `TenantId`.
  3. لا يُسمح بتجاوز الفلتر (`IgnoreQueryFilters`) إلا في خدمات الـPlatform admin الموسومة صراحة.
- **الوصول لمورد Tenant آخر → `404 Not Found`** (لا نكشف وجود المورد).
- **المستخدمون:** هوية عالمية (`User`) + عضوية (`TenantMembership`) لكل عيادة مع أدوار وفروع —
  يدعم طبيباً يعمل في أكثر من عيادة. الـtoken يحمل عضوية واحدة نشطة.

## 4. RBAC

- الصلاحيات ثابتة في الكود (`Permissions.Patients.View`...) لأنها مرتبطة بالـendpoints.
- الأدوار **بيانات** لكل Tenant (`Roles` + `RolePermissions`) تُنسخ من قوالب النظام عند إنشاء العيادة
  (Tenant Owner, Clinic Manager, Doctor, Nurse, Receptionist, Accountant, Staff) ويمكن تخصيصها.
- أدوار المنصة (`SuperAdmin`, `Support`, `BillingAdmin`) منفصلة عن أدوار العيادات.
- `[HasPermission("Patients.View")]` على كل endpoint. الصلاحيات تُحمّل في الـtoken وتُعاد قراءتها عند refresh.
- البيانات الطبية الحساسة (الحساسية، التاريخ المرضي، الزيارات) تتطلب `MedicalRecords.View` — الاستقبال لا يملكها افتراضياً.
- صلاحيات الفرع: العضوية قد تُقيَّد بفروع محددة (`MembershipBranches`)؛ فارغة = كل الفروع.

## 5. SaaS

```text
Plans ──< PlanFeatures (feature key → enabled)
      ──< PlanLimits   (limit key → value | null = غير محدود)
Tenant ──< Subscriptions (Trialing/Active/PastDue/Canceled/Expired)
Tenant ──< TenantFeatureOverrides (تشغيل/إيقاف لكل عيادة)
Environment flags (appsettings) ── أعلى أولوية
```

- `IFeatureGate.IsEnabledAsync(feature)` و `IUsageLimiter.EnsureCanAddAsync(limitKey)` في الـApplication layer.
- تجاوز الحد → `403` بكود `USAGE_LIMIT_REACHED` ورسالة "ترقية الباقة" (بدون انهيار).

## 6. الفوترة والمدفوعات

- `IPaymentGateway` (Create checkout / Verify / Refund / Webhook) مع مزودين قابلين للتبديل (Moyasar, HyperPay, Tap...) — يدعمون مدى/Apple Pay/STC Pay/Visa/Mastercard.
- فواتير العيادة للمريض وفواتير المنصة للعيادة منفصلة.
- VAT: نسبة لكل بند، أسعار شاملة/غير شاملة، الرقم الضريبي للبائع snapshot، ترقيم متسلسل بلا فجوات لكل Tenant عبر `NumberSequences`.
  الجاهزية لـ ZATCA (المرحلة الثانية: UUID, hash chain, QR TLV) مصممة كحقول/خدمة منفصلة تُفعَّل لاحقاً.

## 7. الإشعارات والمهام الخلفية

- Outbox table (`NotificationMessages`) يُكتب داخل نفس الـtransaction، ويُرسل بواسطة `BackgroundService`.
- `INotificationProvider` لكل قناة (Email, SMS, WhatsApp, In-App). المزود يُختار من الإعدادات.

## 8. الوقت والتاريخ

- التخزين: `datetime2` بتوقيت **UTC** دائماً للحظات الزمنية، و`date`/`time` للقيم التقويمية (تاريخ الميلاد، ساعات العمل).
- التحويل إلى `Asia/Riyadh` (أو منطقة العيادة) في طبقة العرض وفي قواعد التوفّر فقط.
- الهجري: عرض فقط (`Intl` بتقويم `islamic-umalqura`) — التخزين ميلادي.

## 9. الأخطاء

صيغة موحدة لكل الأخطاء (المتطلب §40):

```json
{ "success": false, "code": "PATIENT_NOT_FOUND", "message": "المريض غير موجود", "errors": { "mobile": ["..."] }, "correlationId": "..." }
```

لا Stack trace للمستخدم أبداً؛ الرسالة تُترجم حسب `Accept-Language` (ar افتراضياً).

## 10. Frontend

- Next.js (App Router) + TypeScript + Tailwind v4 + shadcn/ui، `dir="rtl"` على مستوى `<html>` مع خصائص CSS منطقية (`ms-`, `pe-`, `start`, `end`).
- خط **IBM Plex Sans Arabic**، Design tokens كمتغيرات CSS.
- `next-intl` لملفات الترجمة `ar.json` / `en.json`.
- الـaccess token في الذاكرة فقط؛ الـrefresh token في Cookie `httpOnly; Secure; SameSite=Strict` — لا أسرار في `localStorage`.
