> **Historical** — audit of the original Payload CMS + MongoDB application ("matab"), which was removed from the
> repository in Phase 2 / Gate C (D16). File paths below refer to that removed code. Current status: [`STATUS.md`](STATUS.md).

# المرحلة 1 — تدقيق البنية (Architecture Audit)

> تاريخ التدقيق: 2026-10-06 · نطاق التدقيق: كامل المستودع (`src/`, `tests/`, ملفات الإعداد)

## 1. ملخص تنفيذي

المشروع الحالي (**matab**) نظام عيادات متعدد المستأجرين مبني على **Payload CMS 3 + Next.js 16 + MongoDB**،
موجّه أصلاً للسوق الباكستاني (PKR / Asia/Karachi). يحتوي على منطق أعمال جيد ومُختبَر
(منع الحجز المزدوج، التوفّر، انتقالات حالة الموعد، حسابات الفاتورة، عزل المستأجرين عبر access functions)،
لكنه **لا يصلح كأساس لمنصة SaaS تجارية سعودية** للأسباب التالية:

| المتطلب | الوضع الحالي | الفجوة |
|---|---|---|
| Backend بطبقات (API/Application/Domain/Infrastructure) | منطق الأعمال داخل hooks الخاصة بـPayload collections | منطق مقترن بالـCMS، غير قابل للاختبار المعزول |
| قاعدة بيانات علائقية (FK, Check, Indexes, Transactions, OFFSET/FETCH) | MongoDB (مستندات + مصفوفات مضمّنة) | لا Referential integrity، الدفعات مخزنة كمصفوفة داخل الفاتورة |
| RBAC بصلاحيات دقيقة | 4 أدوار ثابتة (`superAdmin/owner/doctor/receptionist`) بلا Permissions | لا يمكن تخصيص صلاحيات ولا أدوار ممرض/محاسب/مدير |
| الفروع (Branches) | غير موجودة | Clinic = Tenant بلا فروع |
| الخطط والاشتراكات | مكتوبة في الكود `src/lib/plans.ts` (free/clinic/plus) | Hard-coded، لا Subscription ولا Billing ولا Feature Flags |
| VAT / الفوترة الإلكترونية | غير موجودة | لا ضريبة، لا رقم ضريبي، المبالغ `float` |
| العربية / RTL / i18n | واجهة إنجليزية فقط بنصوص مكتوبة داخل الصفحات | لا ملفات ترجمة |
| Refresh tokens / Lockout / OTP | جلسة JWT واحدة من Payload، بلا refresh rotation | لا قفل حساب مضبوط ولا Rate limit على الدخول |
| Observability | `payload.logger` فقط | لا Correlation ID، لا Health checks، لا Metrics |
| CI/CD | غير موجود | — |

**القرار:** إعادة بناء الـBackend بـ **ASP.NET Core (.NET 10) + EF Core + SQL Server** حسب التفضيل المذكور في المتطلبات،
مع **نقل** (Port) قواعد العمل المُختبرة بدل إعادة اختراعها، وإعادة بناء الـFrontend بـNext.js
مع إعادة استخدام ما يصلح من مكونات UI. التفاصيل في [02-target-architecture.md](02-target-architecture.md)
وخطة الانتقال في [03-migration-plan.md](03-migration-plan.md).

## 2. البنية الحالية

```text
Next.js 16 (App Router)
 ├── (frontend)  صفحات Dashboard + Server Actions  ──┐
 ├── (payload)   Admin panel + REST + GraphQL        ├──► Payload Local API ──► MongoDB (replica set)
 └── api/cron, api/export                           ──┘
```

- **Collections:** `Tenants, Users, Patients, Appointments, Visits, Invoices, AuditLogs`.
- **Server Actions** تستدعي Payload Local API مع `overrideAccess: false` (جيد) — لكن بعض المسارات تستخدم `overrideAccess: true` يدوياً.
- **REST/GraphQL** الخاص بـPayload مفتوح على `/api/*` و`/api/graphql-playground` (محمي بالـaccess functions فقط).

## 3. قاعدة البيانات الحالية

| Collection | ملاحظات |
|---|---|
| tenants | `plan`, `status`, `settings{currency,timezone,...}`, `upgradeRequest` مضمّنة |
| users | `role` نصي، حقول طبيب (specialty, fee, availability) مخلوطة مع حقول المستخدم |
| patients | `mrn` عبر `count + 1` (Race condition)، `allergies/notes` نص حر مرئي لكل الأدوار |
| appointments | Partial unique index `(tenant, doctor, start)` للحالات النشطة — فكرة جيدة تُنقل |
| visits | الوصفة مصفوفة مضمّنة، unique `(tenant, appointment)` |
| invoices | `lineItems[]` و`payments[]` مضمّنة، المبالغ `number` (float)، `invoiceNumber` عبر `count + 1` بلا unique index |
| auditLogs | append-only جيد، لكن بلا IP/Device/CorrelationId/Result |

## 4. المصادقة والتفويض الحاليان

- Payload auth (JWT في Cookie `payload-token`, `httpOnly`, `sameSite=lax`).
- التحقق من البريد للتسجيل الذاتي (يُخزَّن hash التوكن فقط ✅).
- استعادة كلمة المرور عبر Payload (رسالة موحدة لا تكشف وجود الحساب ✅).
- الأدوار: تحقق بالـrole فقط (`user.role === 'owner'`) داخل access functions وصفحات الـDashboard.

## 5. Multi-tenancy الحالي

نقاط قوة يجب الحفاظ على مبادئها:
- **Deny-by-default:** أي طلب لا يحدد Tenant يُرفض.
- **Set it, don't trust it:** `forceTenant` يفرض `tenant` من المستخدم ويمنع نقل السجل بين العيادات.
- اختبارات عزل `tests/int/isolation.int.spec.ts`.

نقاط ضعف:
- الحماية على مستوى access functions الخاصة بـPayload فقط؛ أي استدعاء بـ`overrideAccess: true` يتجاوزها بالكامل (يُستخدم في 20+ موضعاً).
- لا يوجد حاجز في طبقة الوصول للبيانات (Data access layer).

## 6. الثغرات الأمنية المكتشفة

| # | الخطورة | الثغرة | الموقع |
|---|---|---|---|
| S1 | **حرجة** | `secret: process.env.PAYLOAD_SECRET \|\| ''` — إن لم يُضبط المتغير تُوقَّع الـJWT بسر فارغ (تزوير جلسات) | `src/payload.config.ts` |
| S2 | **عالية** | حساب الطبيب التجريبي عند التسجيل: البريد `dr.sample@<tenantId>.example` وكلمة المرور `sample-<tenantId>` — من يعرف البريد يعرف كلمة المرور | `src/lib/signup.ts` |
| S3 | **عالية** | لا Rate limit على تسجيل الدخول ولا إعداد صريح لـLockout | `src/app/(frontend)/login/actions.ts`, `Users.ts` |
| S4 | **عالية** | البيانات الطبية (الحساسية، الملاحظات، الزيارات، التشخيص) مقروءة لكل أدوار العيادة بما فيها الاستقبال — لا صلاحيات على مستوى الحقل | `Patients.ts`, `Visits.ts` |
| S5 | متوسطة | ترقيم الفواتير والمرضى عبر `count + 1` → أرقام مكررة تحت التزامن (الفواتير بلا unique index) — يخالف متطلبات تسلسل الفواتير الضريبية | `Invoices.ts`, `Patients.ts` |
| S6 | متوسطة | Rate limiter في الذاكرة (Process-local) — يسقط عند التوسع الأفقي أو إعادة التشغيل | `src/lib/rateLimit.ts` |
| S7 | متوسطة | `/api/graphql-playground` و REST العام مكشوفان للإنتاج | `src/app/(payload)/api/*` |
| S8 | متوسطة | سجل التدقيق لا يسجل عرض بيانات المريض ولا IP/الجهاز/النتيجة | `src/hooks/audit.ts` |
| S9 | منخفضة | المبالغ المالية `float` (تقريب يدوي) | `Invoices.ts` |
| S10 | منخفضة | `test.env` وقاعدة الاختبار تشارك قاعدة التطوير وتمسحها | `README.md`, `tests/int` |
| S11 | منخفضة | لا Security headers (CSP, HSTS, X-Frame-Options) | `next.config.ts` |

لم يُعثر على: أسرار مكتوبة في الكود، `eval`، أو `dangerouslySetInnerHTML` بمدخلات مستخدم
(الموجود في `components/ui/chart.tsx` يحقن CSS من إعدادات ثابتة — آمن). `localStorage` يخزن تفضيل العرض فقط.

## 7. الـFrontend الحالي

- Tailwind v4 + shadcn/ui (Base UI) + lucide — أساس جيد يُعاد استخدامه.
- LTR فقط، نصوص إنجليزية مكتوبة داخل المكونات، لا i18n.
- مكونات كبيرة تجمع UI وBusiness logic (مثل `DayRail.tsx`, `BookingForm.tsx`).
- الجداول تدعم pagination على الخادم في بعض الصفحات (`TablePager`).

## 8. الدين التقني (Technical Debt)

1. منطق الأعمال مقترن بـPayload hooks (لا يمكن نقله أو اختباره دون Payload).
2. `relID()` مكررة في 4 ملفات.
3. بيانات الطبيب مخلوطة بجدول المستخدمين.
4. الخطط والحدود في الكود.
5. لا فصل بين الإعدادات حسب البيئة (Dev/Staging/Prod).
6. Dockerfile يتطلب `output: 'standalone'` غير مفعّل في `next.config.ts`.
7. لا CI.

## 9. ما يمكن إعادة استخدامه

| العنصر | طريقة إعادة الاستخدام |
|---|---|
| قاعدة التداخل `a.start < b.end && a.end > b.start` + Unique index للحالات النشطة | تُنقل إلى Domain + Filtered unique index في SQL Server |
| آلة حالات الموعد (`STATUS_TRANSITIONS`) | تُنقل إلى Domain مع إضافة `Confirmed` |
| منطق التوفّر (`checkAvailability`, timezone-aware) | يُنقل مع دعم نوافذ متعددة لكل يوم وفروع |
| قواعد الفاتورة (المجاميع مشتقة دائماً، منع الدفع الزائد، قفل البنود بعد الدفع، الإلغاء بسبب فقط) | تُنقل + VAT |
| مبدأ deny-by-default و set-it-don't-trust-it | يُطبّق في EF Core Global Query Filters + SaveChanges interceptor |
| Digest بالتوقيت المحلي لكل عيادة | يُنقل إلى Background job |
| مكونات shadcn/ui | تُعاد استخدامها في الـFrontend الجديد مع RTL |
| سيناريوهات الاختبارات (`tests/int/*`) | تُستخدم كمواصفات لاختبارات xUnit الجديدة |

## 10. ما لا يُعاد استخدامه

- Payload collections/hooks/access functions (الطبقة نفسها).
- MongoDB وإعدادات الـreplica set.
- صفحات الـDashboard بشكلها الحالي (LTR/إنجليزي/منطق مختلط) — يُعاد بناؤها.
- العلامة التجارية "matab" والافتراضات الباكستانية.
