using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using FluentAssertions;

namespace ClinicSaaS.UnitTests;

/// <summary>Visit lifecycle of migration 004: Open → Completed only, and the clinical-edit lock derived from it.</summary>
public class VisitCompletionTests
{
    private static Visit NewVisit() => new()
    {
        TenantId = Guid.NewGuid(), BranchId = Guid.NewGuid(), PatientId = Guid.NewGuid(), DoctorId = Guid.NewGuid(),
        VisitedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public void A_new_visit_is_open_editable_and_carries_no_completion_stamps()
    {
        var v = NewVisit();

        v.Status.Should().Be(VisitStatus.Open);
        v.IsCompleted.Should().BeFalse();
        v.CanEditClinicalContent.Should().BeTrue();
        v.CompletedAtUtc.Should().BeNull();
        v.CompletedByUserId.Should().BeNull();
    }

    [Fact]
    public void Complete_moves_open_to_completed_and_stamps_time_and_user()
    {
        var v = NewVisit();
        var now = new DateTime(2026, 10, 6, 9, 30, 0, DateTimeKind.Utc);
        var user = Guid.NewGuid();

        v.Complete(now, user);

        v.Status.Should().Be(VisitStatus.Completed);
        v.IsCompleted.Should().BeTrue();
        v.CompletedAtUtc.Should().Be(now);
        v.CompletedByUserId.Should().Be(user);
    }

    [Fact]
    public void Complete_accepts_an_unknown_actor()
    {
        var v = NewVisit();

        v.Complete(DateTime.UtcNow, userId: null);

        v.IsCompleted.Should().BeTrue();
        v.CompletedByUserId.Should().BeNull();
        v.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void A_completed_visit_locks_clinical_content()
    {
        var v = NewVisit();
        v.Complete(DateTime.UtcNow, Guid.NewGuid());

        v.CanEditClinicalContent.Should().BeFalse();
    }

    [Fact]
    public void Completing_twice_is_an_invalid_transition_and_keeps_the_original_stamps()
    {
        var v = NewVisit();
        var first = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
        var firstUser = Guid.NewGuid();
        v.Complete(first, firstUser);

        var again = () => v.Complete(first.AddHours(1), Guid.NewGuid());

        again.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.InvalidStatusTransition);
        v.CompletedAtUtc.Should().Be(first);
        v.CompletedByUserId.Should().Be(firstUser);
    }

    [Fact]
    public void A_soft_deleted_open_visit_is_not_editable()
    {
        var v = NewVisit();
        v.IsDeleted = true;

        v.CanEditClinicalContent.Should().BeFalse();
        v.IsCompleted.Should().BeFalse();
    }
}
