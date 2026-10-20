> **Historical** — plan for replacing the original Payload CMS application. The replacement is done and the legacy
> application was removed in Phase 2 / Gate C (D16); no legacy data was migrated. Current status: [`STATUS.md`](STATUS.md).

# خطة الانتقال (Migration Plan)

## المبدأ

بناء النظام الجديد **بجانب** القديم (Strangler pattern): الكود القديم يبقى في جذر المستودع كمرجع حتى تكتمل
المراحل 4–6، ثم يُنقل إلى `legacy/` ويُحذف بعد الإطلاق.

```text
clinic-management-main/
 ├── backend/            ← النظام الجديد (.NET)
 ├── frontend/           ← الواجهة الجديدة (Next.js) — تبدأ في المرحلة 5
 ├── docs/               ← التوثيق (التدقيق، البنية، قاعدة البيانات، القرارات)
 ├── PROGRESS.md         ← متابعة المراحل
 └── src/, tests/ ...    ← النظام القديم (مرجع مؤقت)
```

## ترحيل البيانات

النظام القديم موجَّه لسوق آخر ولا توجد بيانات إنتاج سعودية لترحيلها، لذا **لا يوجد ترحيل بيانات**.
إن وُجدت عيادات حالية لاحقاً: سكربت ETL لمرة واحدة (MongoDB → SQL Server) يربط `ObjectId` القديم بـ`Guid` جديد
عبر جدول mapping مؤقت، ويُختبر على نسخة قبل التشغيل.

## الترتيب

| المرحلة | المخرج الأساسي |
|---|---|
| 1 | هذا التدقيق + البنية المستهدفة + الخطة |
| 2 | حاجز العزل (Query filters + Interceptor)، المصادقة (JWT + refresh rotation + lockout)، RBAC، Rate limiting، اختبارات العزل |
| 3 | المخطط الكامل موثّق + أول Migration للجداول الأساسية + سجل تغييرات المخطط |
| 4 | API لكل domain بصيغة موحدة + Paging/Sorting/Filtering |
| 5 | Frontend جديد RTL + Design system |
| 6 | المرضى، الأطباء، المواعيد، الزيارات، الوصفات، الملفات |
| 7 | الخطط، الاشتراكات، Feature flags، حدود الاستخدام |
| 8 | فواتير VAT + بوابة دفع سعودية عبر abstraction |
| 9 | Outbox + Email/SMS/WhatsApp + OTP |
| 10 | التوطين الكامل (ar/en، الهجري، SAR) |
| 11 | E2E (Playwright) + تغطية الاختبارات |
| 12 | الأداء (Execution plans، caching، virtualization) |
| 13 | Logs/Metrics/Tracing (OpenTelemetry) |
| 14 | Docker، البيئات، CI/CD، النسخ الاحتياطي واختبار الاستعادة |
| 15 | تدقيق أمني نهائي (OWASP ASVS checklist) |

## قاعدة تغيير المخطط

كل تغيير في قاعدة البيانات يمر بـ: المخطط الحالي → تحليل الأثر → المبرر → Migration → اختبارات → تطبيق،
ويُسجَّل في [database/schema-changelog.md](database/schema-changelog.md).
