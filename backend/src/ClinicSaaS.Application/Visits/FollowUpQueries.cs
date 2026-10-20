using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Clinical;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Visits;

/// <summary>Follow-ups due between two clinic-local dates (inclusive). Defaults: today … today + 7 days.</summary>
public sealed record FollowUpQuery : PageQuery
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public Guid? DoctorId { get; init; }
    public Guid? BranchId { get; init; }
    /// <summary>true = only follow-ups that already have a later appointment; false = only those still to book.</summary>
    public bool? Booked { get; init; }
}

/// <summary>
/// One follow-up, operational fields only. Deliberately no visit id, diagnosis, notes, medications, allergies or any
/// other clinical content: reception books follow-ups without Visits.View or MedicalRecords.View.
/// </summary>
public sealed record FollowUpItem(
    Guid PatientId, string PatientName, string PatientMrn, DateOnly FollowUpDate,
    Guid DoctorId, string DoctorName, Guid BranchId, bool Booked, DateTime? NextAppointmentUtc);

public sealed class FollowUpQueryValidator : AbstractValidator<FollowUpQuery>
{
    public const int MaxRangeDays = 92;

    public FollowUpQueryValidator()
    {
        RuleFor(x => x.To).Must((q, to) => q.From is null || to is null || to >= q.From).WithMessage(ValidationCodes.OutOfRange);
        RuleFor(x => x.To).Must((q, to) => q.From is null || to is null || to.Value.DayNumber - q.From.Value.DayNumber <= MaxRangeDays)
            .WithMessage(ValidationCodes.OutOfRange);
    }
}

/// <summary>
/// The follow-up list for the front desk (Appointments.View). Reads the follow-up date doctors record on visits and
/// says whether the patient already has a later appointment. Same row scope as visits: tenant filter, the member's
/// branches, and a doctor without ViewAllDoctors sees only their own patients' follow-ups.
/// </summary>
public sealed class FollowUpQueries(IAppDbContext db, ClinicContext clinic, IClock clock, IValidator<FollowUpQuery> validator)
{
    private static readonly AppointmentStatus[] Booking =
        [AppointmentStatus.Scheduled, AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn, AppointmentStatus.Completed];

    public async Task<PagedResult<FollowUpItem>> ListAsync(FollowUpQuery query, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(query, ct);
        var zone = await clinic.GetTimeZoneAsync(ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, zone));
        var from = query.From ?? today;
        var to = query.To ?? (query.From ?? today).AddDays(7);
        if (to.DayNumber - from.DayNumber > FollowUpQueryValidator.MaxRangeDays) to = from.AddDays(FollowUpQueryValidator.MaxRangeDays);

        var q = db.Visits.AsNoTracking().Where(v => v.FollowUpDate != null && v.FollowUpDate >= from && v.FollowUpDate <= to);
        if (query.DoctorId is { } d) q = q.Where(v => v.DoctorId == d);
        if (query.BranchId is { } b) q = q.Where(v => v.BranchId == b);
        var allowed = clinic.RestrictedBranchIds;
        if (allowed.Count > 0) q = q.Where(v => allowed.Contains(v.BranchId));
        if (await clinic.OwnDoctorScopeAsync(ct) is { } ownDoctor) q = q.Where(v => v.DoctorId == ownDoctor);

        var rows = q.Select(v => new
        {
            v.PatientId, v.FollowUpDate, v.DoctorId, v.BranchId, v.VisitedAtUtc,
            Next = db.Appointments
                .Where(a => a.PatientId == v.PatientId && a.StartUtc > v.VisitedAtUtc && Booking.Contains(a.Status))
                .OrderBy(a => a.StartUtc).Select(a => (DateTime?)a.StartUtc).FirstOrDefault(),
        });
        if (query.Booked is true) rows = rows.Where(r => r.Next != null);
        if (query.Booked is false) rows = rows.Where(r => r.Next == null);

        return await rows.OrderBy(r => r.FollowUpDate).ThenBy(r => r.VisitedAtUtc).ToPagedResultAsync(query, r => new FollowUpItem(
            r.PatientId,
            db.Patients.Where(p => p.Id == r.PatientId).Select(p => p.FullName).FirstOrDefault() ?? "",
            db.Patients.Where(p => p.Id == r.PatientId).Select(p => p.MedicalRecordNumber).FirstOrDefault() ?? "",
            r.FollowUpDate!.Value,
            r.DoctorId,
            db.Doctors.Where(x => x.Id == r.DoctorId).Select(x => x.DisplayNameAr).FirstOrDefault() ?? "",
            r.BranchId,
            r.Next != null,
            r.Next), ct);
    }
}
