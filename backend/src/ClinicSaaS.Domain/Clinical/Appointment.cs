using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Clinical;

public enum AppointmentStatus { Scheduled, Confirmed, CheckedIn, Completed, Cancelled, NoShow }
public enum AppointmentSource { Reception, Phone, Online, WalkIn }

public sealed class Appointment : TenantEntity, IConcurrencyAware
{
    public Guid BranchId { get; set; }
    public Guid PatientId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid? ServiceId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;
    public AppointmentSource Source { get; set; } = AppointmentSource.Reception;
    /// <summary>Walk-in queue token per clinic per day (T-01…).</summary>
    public string? TokenNumber { get; set; }
    public string? Reason { get; set; }
    public string? CancellationReason { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public DateTime? CheckedInAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    /// <summary>Legal status transitions. Terminal states have no outgoing edges.</summary>
    public static readonly IReadOnlyDictionary<AppointmentStatus, AppointmentStatus[]> Transitions =
        new Dictionary<AppointmentStatus, AppointmentStatus[]>
        {
            [AppointmentStatus.Scheduled] = [AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn, AppointmentStatus.Cancelled, AppointmentStatus.NoShow],
            [AppointmentStatus.Confirmed] = [AppointmentStatus.CheckedIn, AppointmentStatus.Cancelled, AppointmentStatus.NoShow],
            [AppointmentStatus.CheckedIn] = [AppointmentStatus.Completed, AppointmentStatus.Cancelled],
            [AppointmentStatus.Completed] = [],
            [AppointmentStatus.Cancelled] = [],
            [AppointmentStatus.NoShow] = [],
        };

    /// <summary>Statuses that occupy the doctor's slot (used by the double-booking guard and the filtered unique index).</summary>
    public static readonly AppointmentStatus[] SlotOccupyingStatuses =
        [AppointmentStatus.Scheduled, AppointmentStatus.Confirmed, AppointmentStatus.CheckedIn];

    public bool OccupiesSlot => SlotOccupyingStatuses.Contains(Status) && Source != AppointmentSource.WalkIn;

    public static bool CanTransition(AppointmentStatus from, AppointmentStatus to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public void TransitionTo(AppointmentStatus to, DateTime nowUtc, string? reason = null)
    {
        if (to == Status) return;
        if (!CanTransition(Status, to)) throw new DomainException(ErrorCodes.InvalidStatusTransition, $"{Status} -> {to}");

        switch (to)
        {
            case AppointmentStatus.Cancelled:
                if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.CancellationReasonRequired);
                CancellationReason = reason.Trim();
                CancelledAtUtc = nowUtc;
                break;
            case AppointmentStatus.Confirmed:
                ConfirmedAtUtc = nowUtc;
                break;
            case AppointmentStatus.CheckedIn:
                CheckedInAtUtc = nowUtc;
                break;
            case AppointmentStatus.Completed:
                CompletedAtUtc = nowUtc;
                break;
        }
        Status = to;
    }

    /// <summary>Half-open interval overlap: touching edges (10:00–10:15, 10:15–10:30) do not conflict.</summary>
    public static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd) =>
        aStart < bEnd && aEnd > bStart;
}
