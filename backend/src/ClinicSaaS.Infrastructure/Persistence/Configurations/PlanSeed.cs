using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClinicSaaS.Infrastructure.Persistence.Configurations;

/// <summary>
/// Initial plan catalog. This is *seed data*, not logic: super admins edit plans in the database afterwards and
/// nothing in the code branches on a plan code. Prices are placeholders pending the commercial decision (SAR, ex-VAT).
/// </summary>
internal sealed class PlanSeed : IEntityTypeConfiguration<Plan>
{
    public static readonly Guid Free = Guid.Parse("0b6a4a3e-0000-4000-8000-000000000001");
    public static readonly Guid Basic = Guid.Parse("0b6a4a3e-0000-4000-8000-000000000002");
    public static readonly Guid Professional = Guid.Parse("0b6a4a3e-0000-4000-8000-000000000003");
    public static readonly Guid Enterprise = Guid.Parse("0b6a4a3e-0000-4000-8000-000000000004");

    private static readonly DateTime SeededAt = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.HasData(
            P(Free, "free", "المجانية", "Free", 0, 0, 0, 1),
            P(Basic, "basic", "الأساسية", "Basic", 199, 1990, 14, 2),
            P(Professional, "professional", "الاحترافية", "Professional", 449, 4490, 14, 3),
            P(Enterprise, "enterprise", "المؤسسات", "Enterprise", 1499, 14990, 14, 4));
    }

    private static Plan P(Guid id, string code, string ar, string en, decimal monthly, decimal yearly, int trial, int order) => new()
    {
        Id = id, Code = code, NameAr = ar, NameEn = en, MonthlyPrice = monthly, YearlyPrice = yearly,
        TrialDays = trial, SortOrder = order, IsPublic = true, IsActive = true, CreatedAtUtc = SeededAt,
    };

    internal sealed class Features : IEntityTypeConfiguration<PlanFeature>
    {
        public void Configure(EntityTypeBuilder<PlanFeature> b)
        {
            var rows = new List<PlanFeature>();
            void Add(Guid plan, params string[] keys) => rows.AddRange(keys.Select(k => new PlanFeature { PlanId = plan, FeatureKey = k }));

            Add(Free, FeatureKeys.Invoicing);
            Add(Basic, FeatureKeys.Invoicing, FeatureKeys.OnlineBooking, FeatureKeys.Export);
            Add(Professional, FeatureKeys.Invoicing, FeatureKeys.OnlineBooking, FeatureKeys.Export, FeatureKeys.Reports,
                FeatureKeys.SmsNotifications, FeatureKeys.WhatsAppNotifications, FeatureKeys.AuditLogs, FeatureKeys.MultipleBranches,
                FeatureKeys.PublicPageCustomization);
            Add(Enterprise, [.. FeatureKeys.All]);
            b.HasData(rows);
        }
    }

    internal sealed class Limits : IEntityTypeConfiguration<PlanLimit>
    {
        public void Configure(EntityTypeBuilder<PlanLimit> b)
        {
            var rows = new List<PlanLimit>();
            void Set(Guid plan, int? users, int? doctors, int? patients, int? apptsPerMonth, int? branches, int? storageMb, int? sms, int? whatsapp)
            {
                rows.Add(new PlanLimit { PlanId = plan, LimitKey = LimitKeys.MaxUsers, MaxValue = users });
                rows.Add(new PlanLimit { PlanId = plan, LimitKey = LimitKeys.MaxDoctors, MaxValue = doctors });
                rows.Add(new PlanLimit { PlanId = plan, LimitKey = LimitKeys.MaxPatients, MaxValue = patients });
                rows.Add(new PlanLimit { PlanId = plan, LimitKey = LimitKeys.MaxAppointmentsPerMonth, MaxValue = apptsPerMonth });
                rows.Add(new PlanLimit { PlanId = plan, LimitKey = LimitKeys.MaxBranches, MaxValue = branches });
                rows.Add(new PlanLimit { PlanId = plan, LimitKey = LimitKeys.MaxStorageMb, MaxValue = storageMb });
                rows.Add(new PlanLimit { PlanId = plan, LimitKey = LimitKeys.MaxSmsPerMonth, MaxValue = sms });
                rows.Add(new PlanLimit { PlanId = plan, LimitKey = LimitKeys.MaxWhatsAppPerMonth, MaxValue = whatsapp });
            }

            //        users doctors patients appts/mo branches storageMB sms  whatsapp
            Set(Free,         1,     1,     100,     150,     1,      200,    0,    0);
            Set(Basic,        3,     2,    3000,    1500,     1,     2048,  100,    0);
            Set(Professional, 15,   10,    null,    null,     3,    20480, 1000, 1000);
            Set(Enterprise, null, null,    null,    null,  null,   102400, null, null);
            b.HasData(rows);
        }
    }
}
