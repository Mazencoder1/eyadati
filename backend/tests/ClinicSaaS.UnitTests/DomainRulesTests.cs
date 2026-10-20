using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using FluentAssertions;

namespace ClinicSaaS.UnitTests;

public class SaudiFormatsTests
{
    [Theory]
    [InlineData("0501234567", "+966501234567")]
    [InlineData("501234567", "+966501234567")]
    [InlineData("966501234567", "+966501234567")]
    [InlineData("+966 50 123 4567", "+966501234567")]
    [InlineData("00966501234567", "+966501234567")]
    [InlineData("050-123-4567", "+966501234567")]
    [InlineData("٠٥٠١٢٣٤٥٦٧", "+966501234567")] // Arabic-Indic digits
    public void NormalizeMobile_accepts_common_saudi_formats(string input, string expected) =>
        SaudiFormats.NormalizeMobile(input).Should().Be(expected);

    [Theory]
    [InlineData("0111234567")]      // landline
    [InlineData("05012345")]        // too short
    [InlineData("05012345678")]     // too long
    [InlineData("+971501234567")]   // UAE
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeMobile_rejects_non_saudi_mobiles(string? input) =>
        SaudiFormats.NormalizeMobile(input).Should().BeNull();

    [Theory]
    [InlineData("1000000008", true)]  // valid check digit, citizen
    [InlineData("2000000006", true)]  // valid check digit, resident (Iqama)
    [InlineData("1000000001", false)] // bad check digit
    [InlineData("3000000004", false)] // must start with 1 or 2
    [InlineData("100000000", false)]  // 9 digits
    [InlineData("abcdefghij", false)]
    public void NationalId_check_digit(string id, bool valid) =>
        SaudiFormats.IsValidNationalIdOrIqama(id).Should().Be(valid);

    [Theory]
    [InlineData("300000000000003", true)]
    [InlineData("310123456700003", true)]
    [InlineData("300000000000004", false)]
    [InlineData("200000000000003", false)]
    [InlineData("30000000000003", false)]
    public void VatNumber_format(string vat, bool valid) => SaudiFormats.IsValidVatNumber(vat).Should().Be(valid);
}

public class AppointmentRulesTests
{
    private static readonly DateTime T = new(2026, 10, 6, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Touching_edges_do_not_overlap() =>
        Appointment.Overlaps(T, T.AddMinutes(15), T.AddMinutes(15), T.AddMinutes(30)).Should().BeFalse();

    [Fact]
    public void Partial_overlap_is_detected() =>
        Appointment.Overlaps(T, T.AddMinutes(15), T.AddMinutes(10), T.AddMinutes(25)).Should().BeTrue();

    [Fact]
    public void Containment_is_detected() =>
        Appointment.Overlaps(T, T.AddMinutes(60), T.AddMinutes(10), T.AddMinutes(20)).Should().BeTrue();

    [Theory]
    [InlineData(AppointmentStatus.Scheduled, AppointmentStatus.Confirmed, true)]
    [InlineData(AppointmentStatus.Scheduled, AppointmentStatus.CheckedIn, true)]
    [InlineData(AppointmentStatus.Confirmed, AppointmentStatus.NoShow, true)]
    [InlineData(AppointmentStatus.CheckedIn, AppointmentStatus.Completed, true)]
    [InlineData(AppointmentStatus.Scheduled, AppointmentStatus.Completed, false)]
    [InlineData(AppointmentStatus.Completed, AppointmentStatus.Cancelled, false)]
    [InlineData(AppointmentStatus.Cancelled, AppointmentStatus.Scheduled, false)]
    [InlineData(AppointmentStatus.NoShow, AppointmentStatus.CheckedIn, false)]
    public void Status_transitions(AppointmentStatus from, AppointmentStatus to, bool allowed) =>
        Appointment.CanTransition(from, to).Should().Be(allowed);

    [Fact]
    public void Illegal_transition_throws_with_code()
    {
        var a = new Appointment { Status = AppointmentStatus.Completed };
        var act = () => a.TransitionTo(AppointmentStatus.Scheduled, T);
        act.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.InvalidStatusTransition);
    }

    [Fact]
    public void Cancelling_requires_a_reason_and_stamps_time()
    {
        var a = new Appointment();
        a.Invoking(x => x.TransitionTo(AppointmentStatus.Cancelled, T, " "))
            .Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.CancellationReasonRequired);

        a.TransitionTo(AppointmentStatus.Cancelled, T, " patient travelling ");
        a.Status.Should().Be(AppointmentStatus.Cancelled);
        a.CancellationReason.Should().Be("patient travelling");
        a.CancelledAtUtc.Should().Be(T);
    }

    [Fact]
    public void Walk_ins_and_terminal_states_do_not_occupy_the_slot()
    {
        new Appointment { Status = AppointmentStatus.Scheduled }.OccupiesSlot.Should().BeTrue();
        new Appointment { Status = AppointmentStatus.Scheduled, Source = AppointmentSource.WalkIn }.OccupiesSlot.Should().BeFalse();
        new Appointment { Status = AppointmentStatus.Cancelled }.OccupiesSlot.Should().BeFalse();
    }

    [Fact]
    public void Schedule_window_coverage()
    {
        var s = new DoctorSchedule { DayOfWeek = DayOfWeek.Sunday, StartTime = new(16, 0), EndTime = new(21, 0) };
        s.Covers(DayOfWeek.Sunday, new(16, 0), new(16, 15)).Should().BeTrue();
        s.Covers(DayOfWeek.Sunday, new(20, 50), new(21, 5)).Should().BeFalse();
        s.Covers(DayOfWeek.Monday, new(16, 0), new(16, 15)).Should().BeFalse();
    }
}

public class UserLockoutTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

    private static User NewUser() => new() { Email = "a@b.sa", NormalizedEmail = "A@B.SA", FullName = "A" };

    [Fact]
    public void Locks_after_max_failures_and_unlocks_after_duration()
    {
        var u = NewUser();
        for (var i = 0; i < 4; i++) u.RegisterFailedLogin(Now, 5, TimeSpan.FromMinutes(15));
        u.IsLockedOut(Now).Should().BeFalse();

        u.RegisterFailedLogin(Now, 5, TimeSpan.FromMinutes(15));
        u.IsLockedOut(Now).Should().BeTrue();
        u.IsLockedOut(Now.AddMinutes(16)).Should().BeFalse();
    }

    [Fact]
    public void Successful_login_resets_counter()
    {
        var u = NewUser();
        u.RegisterFailedLogin(Now, 5, TimeSpan.FromMinutes(15));
        u.RegisterSuccessfulLogin(Now);
        u.AccessFailedCount.Should().Be(0);
        u.LastLoginAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Password_change_rotates_security_stamp()
    {
        var u = NewUser();
        var stamp = u.SecurityStamp;
        u.ChangePasswordHash("new-hash");
        u.SecurityStamp.Should().NotBe(stamp);
    }
}

public class PermissionModelTests
{
    [Fact]
    public void Catalog_has_no_duplicates_and_excludes_platform_permissions()
    {
        Permissions.AllTenant.Should().OnlyHaveUniqueItems();
        Permissions.AllTenant.Should().NotContain(p => p.StartsWith("Platform."));
        Permissions.AllPlatform.Should().OnlyHaveUniqueItems().And.OnlyContain(p => p.StartsWith("Platform."));
    }

    [Fact]
    public void Owner_template_has_every_tenant_permission() =>
        SystemRoles.Get(SystemRoles.TenantOwner).Permissions.Should().BeEquivalentTo(Permissions.AllTenant);

    [Fact]
    public void Receptionist_cannot_read_clinical_data_or_delete_patients()
    {
        var p = SystemRoles.Get(SystemRoles.Receptionist).Permissions;
        p.Should().Contain(Permissions.Patients.Create);
        p.Should().NotContain([Permissions.MedicalRecords.View, Permissions.Visits.View, Permissions.Patients.Delete]);
    }

    [Fact]
    public void Manager_cannot_manage_subscription_or_roles()
    {
        var p = SystemRoles.Get(SystemRoles.ClinicManager).Permissions;
        p.Should().NotContain([Permissions.Subscription.Manage, Permissions.Roles.Manage]);
    }

    [Fact]
    public void Templates_only_reference_catalog_permissions() =>
        SystemRoles.Templates.SelectMany(t => t.Permissions).Should().OnlyContain(p => Permissions.AllTenant.Contains(p));

    [Fact]
    public void Platform_roles_never_get_tenant_permissions() =>
        Enum.GetValues<PlatformRole>().SelectMany(SystemRoles.PlatformPermissions).Should().NotContain(p => Permissions.IsTenantPermission(p));
}

public class SubscriptionTests
{
    [Fact]
    public void Trial_plan_starts_trialing_until_trial_end()
    {
        var now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        var plan = new Plan { Id = Guid.NewGuid(), Code = "basic", NameAr = "x", NameEn = "x", TrialDays = 14 };
        var s = Subscription.StartTrial(Guid.NewGuid(), plan, now);
        s.Status.Should().Be(SubscriptionStatus.Trialing);
        s.TrialEndsAtUtc.Should().Be(now.AddDays(14));
        s.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void Plan_without_trial_starts_active_for_a_month()
    {
        var now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        var plan = new Plan { Id = Guid.NewGuid(), Code = "free", NameAr = "x", NameEn = "x", TrialDays = 0 };
        var s = Subscription.StartTrial(Guid.NewGuid(), plan, now);
        s.Status.Should().Be(SubscriptionStatus.Active);
        s.CurrentPeriodEndUtc.Should().Be(now.AddMonths(1));
    }
}

public class FileScanStatusTests
{
    private static FileAttachment NewFile() => new()
    {
        FileName = "lab.pdf", ContentType = "application/pdf", SizeBytes = 10, StorageKey = "k", Sha256 = new string('a', 64),
    };

    [Fact]
    public void New_files_start_pending_and_are_not_downloadable()
    {
        var f = NewFile();
        f.ScanStatus.Should().Be(FileScanStatus.Pending);
        f.CanDownload.Should().BeFalse();
    }

    [Theory]
    [InlineData(FileScanStatus.Pending)]
    [InlineData(FileScanStatus.Scanning)]
    [InlineData(FileScanStatus.Rejected)]
    [InlineData(FileScanStatus.Failed)]
    public void Only_clean_files_are_downloadable(FileScanStatus status)
    {
        var f = NewFile();
        foreach (var step in PathTo(status)) f.TransitionScanTo(step);
        f.ScanStatus.Should().Be(status);
        f.CanDownload.Should().BeFalse();
    }

    [Fact]
    public void Clean_file_is_downloadable_until_deleted()
    {
        var f = NewFile();
        foreach (var step in PathTo(FileScanStatus.Clean)) f.TransitionScanTo(step);
        f.CanDownload.Should().BeTrue();
        f.IsDeleted = true;
        f.CanDownload.Should().BeFalse();
    }

    [Theory]
    [InlineData(FileScanStatus.Pending, FileScanStatus.Clean)]     // must be scanned first
    [InlineData(FileScanStatus.Pending, FileScanStatus.Rejected)]
    [InlineData(FileScanStatus.Failed, FileScanStatus.Clean)]      // a failed scan is retried, never assumed clean
    [InlineData(FileScanStatus.Rejected, FileScanStatus.Pending)]  // a rejected file is never re-admitted
    [InlineData(FileScanStatus.Rejected, FileScanStatus.Clean)]
    [InlineData(FileScanStatus.Clean, FileScanStatus.Pending)]
    [InlineData(FileScanStatus.Clean, FileScanStatus.Rejected)]
    public void Illegal_scan_transitions_are_rejected(FileScanStatus from, FileScanStatus to)
    {
        var f = NewFile();
        foreach (var step in PathTo(from)) f.TransitionScanTo(step);
        var act = () => f.TransitionScanTo(to);
        act.Should().Throw<DomainException>().Which.Code.Should().Be(ErrorCodes.InvalidStatusTransition);
        f.ScanStatus.Should().Be(from);
    }

    [Fact]
    public void Failed_scan_can_be_retried()
    {
        var f = NewFile();
        f.TransitionScanTo(FileScanStatus.Scanning);
        f.TransitionScanTo(FileScanStatus.Failed);
        f.TransitionScanTo(FileScanStatus.Pending);
        f.TransitionScanTo(FileScanStatus.Scanning);
        f.TransitionScanTo(FileScanStatus.Clean);
        f.CanDownload.Should().BeTrue();
    }

    private static FileScanStatus[] PathTo(FileScanStatus target) => target switch
    {
        FileScanStatus.Pending => [],
        FileScanStatus.Scanning => [FileScanStatus.Scanning],
        FileScanStatus.Clean => [FileScanStatus.Scanning, FileScanStatus.Clean],
        FileScanStatus.Rejected => [FileScanStatus.Scanning, FileScanStatus.Rejected],
        FileScanStatus.Failed => [FileScanStatus.Scanning, FileScanStatus.Failed],
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };
}
