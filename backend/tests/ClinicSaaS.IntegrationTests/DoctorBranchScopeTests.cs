using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// T-011 (F2 product decision): doctors and working hours are branch-contextual for branch-restricted members.
/// The two-branch clinic's owner-doctor works at BOTH branches (main 08–14, second 15–22) and is "shared".
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DoctorBranchScopeTests(ApiFactory factory)
{
    [Fact]
    public async Task Restricted_member_sees_a_shared_doctor_with_only_their_branchs_windows()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (reception, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist", [t.SecondBranchId]);

        var one = await (await reception.GetAsync($"/api/v1/doctors/{t.DoctorId}")).DataAsync(200);
        Branches(one).Should().OnlyContain(b => b == t.SecondBranchId).And.NotBeEmpty();
        var listed = (await (await reception.GetAsync("/api/v1/doctors?pageSize=100")).DataAsync(200)).EnumerateArray()
            .Single(d => d.GetProperty("id").GetGuid() == t.DoctorId);
        Branches(listed).Should().OnlyContain(b => b == t.SecondBranchId);

        // The owner (unrestricted) still sees both branches' windows.
        Branches(await (await t.OwnerClient.GetAsync($"/api/v1/doctors/{t.DoctorId}")).DataAsync(200)).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task Restricted_manager_schedule_update_changes_only_their_branch_and_preserves_the_others()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);
        var url = $"/api/v1/doctors/{t.DoctorId}";

        // A window in a branch the manager can't access is refused, and nothing changes.
        var before = await (await t.OwnerClient.GetAsync(url)).DataAsync(200);
        var foreign = await manager.PutAsJsonAsync($"{url}/schedule", new
        {
            windows = new[] { new { branchId = t.MainBranchId, dayOfWeek = "Sunday", startTime = "06:00:00", endTime = "07:00:00" } },
            rowVersion = await manager.RowVersionAsync(url),
        });
        foreign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await foreign.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");
        (await (await t.OwnerClient.GetAsync(url)).DataAsync(200)).GetProperty("rowVersion").GetString().Should().Be(before.GetProperty("rowVersion").GetString());

        // Replacing "the schedule" with one second-branch window replaces only the second-branch windows.
        var res = await manager.PutAsJsonAsync($"{url}/schedule", new
        {
            windows = new[] { new { branchId = t.SecondBranchId, dayOfWeek = "Monday", startTime = "16:00:00", endTime = "18:00:00" } },
            rowVersion = await manager.RowVersionAsync(url),
        });
        var visible = await res.DataAsync(200);
        visible.GetProperty("schedule").GetArrayLength().Should().Be(1);

        var owner = await (await t.OwnerClient.GetAsync(url)).DataAsync(200);
        var windows = owner.GetProperty("schedule").EnumerateArray().ToList();
        windows.Where(w => w.GetProperty("branchId").GetGuid() == t.MainBranchId).Should().HaveCount(7, "the main-branch week is preserved");
        windows.Where(w => w.GetProperty("branchId").GetGuid() == t.SecondBranchId).Should().ContainSingle()
            .Which.GetProperty("startTime").GetString().Should().Be("16:00:00");

        // An empty list clears only the manager's branch; the doctor stays visible to the owner with the main week.
        await (await manager.PutAsJsonAsync($"{url}/schedule", new { windows = Array.Empty<object>(), rowVersion = await manager.RowVersionAsync(url) }))
            .EnsureStatusAsync(200);
        Branches(await (await t.OwnerClient.GetAsync(url)).DataAsync(200)).Should().HaveCount(7).And.OnlyContain(b => b == t.MainBranchId);
    }

    [Fact]
    public async Task Restricted_manager_schedule_update_still_prevents_overlap_with_hidden_windows()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);
        var url = $"/api/v1/doctors/{t.DoctorId}";

        // 10:00–11:00 collides with the doctor's (invisible to the manager) main-branch 08:00–14:00.
        var res = await manager.PutAsJsonAsync($"{url}/schedule", new
        {
            windows = new[] { new { branchId = t.SecondBranchId, dayOfWeek = "Sunday", startTime = "10:00:00", endTime = "11:00:00" } },
            rowVersion = await manager.RowVersionAsync(url),
        });
        (await res.ErrorCodeAsync()).Should().Be("SCHEDULE_OVERLAP");
    }

    [Fact]
    public async Task Restricted_manager_edits_the_profile_only_of_doctors_working_solely_in_their_branches()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);

        // Shared doctor (also works at main): deactivation would affect the other branch.
        var shared = await (await manager.GetAsync($"/api/v1/doctors/{t.DoctorId}")).DataAsync(200);
        var sharedEdit = await manager.PutAsJsonAsync($"/api/v1/doctors/{t.DoctorId}", ProfileUpdate(shared, isActive: false));
        sharedEdit.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sharedEdit.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");

        // Doctor working only in the manager's branch: allowed.
        var (_, membership, _) = await factory.InviteAndAcceptAsync(t.Owner, "doctor");
        var own = await t.OwnerClient.CreateDoctorAsync(membership,
            new[] { new { branchId = t.SecondBranchId, dayOfWeek = "Sunday", startTime = "15:00:00", endTime = "16:00:00" } });
        var ownEdit = await manager.PutAsJsonAsync($"/api/v1/doctors/{own.GetProperty("id").GetGuid()}", ProfileUpdate(own, isActive: false));
        (await ownEdit.DataAsync(200)).GetProperty("isActive").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Doctor_without_schedule_is_visible_to_restricted_members_and_can_be_given_hours_in_their_branch()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);
        var (_, membership, _) = await factory.InviteAndAcceptAsync(t.Owner, "doctor");

        // Creating with hours in another branch is refused; without hours it works.
        var foreign = await manager.PostAsJsonAsync("/api/v1/doctors", new
        {
            membershipId = membership, displayNameAr = "د. جديد", specialty = "باطنة", consultationFee = 100m, defaultSlotMinutes = 15,
            acceptsOnlineBooking = true,
            schedule = new[] { new { branchId = t.MainBranchId, dayOfWeek = "Sunday", startTime = "08:00:00", endTime = "09:00:00" } },
        });
        (await foreign.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");
        var created = await manager.CreateDoctorAsync(membership, schedule: null);
        var id = created.GetProperty("id").GetGuid();

        (await (await manager.GetAsync("/api/v1/doctors?pageSize=100")).DataAsync(200)).EnumerateArray()
            .Select(d => d.GetProperty("id").GetGuid()).Should().Contain(id, "a doctor with no schedule is not tied to any branch");
        await (await manager.PutAsJsonAsync($"/api/v1/doctors/{id}/schedule", new
        {
            windows = new[] { new { branchId = t.SecondBranchId, dayOfWeek = "Sunday", startTime = "15:00:00", endTime = "16:00:00" } },
            rowVersion = await manager.RowVersionAsync($"/api/v1/doctors/{id}"),
        })).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task Restricted_manager_edits_own_branch_but_cannot_create_branches()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);

        var second = (await (await manager.GetAsync("/api/v1/branches")).DataAsync(200)).EnumerateArray()
            .Single(b => b.GetProperty("id").GetGuid() == t.SecondBranchId);
        await (await manager.PutAsJsonAsync($"/api/v1/branches/{t.SecondBranchId}", new
        {
            name = "فرع الشمال - معدل", isActive = true, rowVersion = second.GetProperty("rowVersion").GetString(),
        })).EnsureStatusAsync(200);

        var create = await manager.PostAsJsonAsync("/api/v1/branches", new { name = "فرع ثالث" });
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await create.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");
    }

    private static List<Guid> Branches(JsonElement doctor) =>
        doctor.GetProperty("schedule").EnumerateArray().Select(w => w.GetProperty("branchId").GetGuid()).ToList();

    private static object ProfileUpdate(JsonElement d, bool isActive) => new
    {
        displayNameAr = d.GetProperty("displayNameAr").GetString(), specialty = d.GetProperty("specialty").GetString(),
        consultationFee = 0m, defaultSlotMinutes = 15, acceptsOnlineBooking = false, isActive,
        rowVersion = d.GetProperty("rowVersion").GetString(),
    };
}
