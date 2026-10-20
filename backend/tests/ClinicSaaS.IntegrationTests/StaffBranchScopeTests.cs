using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// T-009 (QA-01 + QA-04): a branch-restricted staff manager can only hand out, keep or change access inside their own
/// branches, and only manages members whose access lies entirely inside them. Unrestricted actors are unchanged.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StaffBranchScopeTests(ApiFactory factory)
{
    [Fact]
    public async Task Restricted_manager_can_invite_and_edit_staff_within_own_branches()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);
        var receptionist = await t.OwnerClient.RoleIdAsync("receptionist");
        var nurse = await t.OwnerClient.RoleIdAsync("nurse");

        var invited = await (await manager.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "موظف الفرع", email = TestApi.UniqueEmail("branchstaff"), roleIds = new[] { receptionist }, branchIds = new[] { t.SecondBranchId },
        })).DataAsync(201);
        invited.GetProperty("branchIds").EnumerateArray().Select(b => b.GetGuid()).Should().Equal(t.SecondBranchId);

        var seat = invited.GetProperty("membershipId").GetGuid();
        var edited = await manager.PutAsJsonAsync($"/api/v1/staff/{seat}", new
        {
            roleIds = new[] { nurse }, branchIds = new[] { t.SecondBranchId }, rowVersion = await manager.RowVersionAsync($"/api/v1/staff/{seat}"),
        });
        (await edited.DataAsync(200)).GetProperty("roles").EnumerateArray().Single().GetProperty("id").GetGuid().Should().Be(nurse);
    }

    [Fact]
    public async Task Restricted_manager_cannot_move_a_member_to_another_branch_or_add_one()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);
        var (_, seat, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist", [t.SecondBranchId]);
        var receptionist = await t.OwnerClient.RoleIdAsync("receptionist");

        foreach (var branchIds in new[] { new[] { t.MainBranchId }, new[] { t.SecondBranchId, t.MainBranchId } })
        {
            var res = await manager.PutAsJsonAsync($"/api/v1/staff/{seat}", new
            {
                roleIds = new[] { receptionist }, branchIds, rowVersion = await manager.RowVersionAsync($"/api/v1/staff/{seat}"),
            });
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await res.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");
        }

        // Omitting branchIds means "no restriction" (every branch): refused as well.
        var omitted = await manager.PutAsJsonAsync($"/api/v1/staff/{seat}", new
        {
            roleIds = new[] { receptionist }, rowVersion = await manager.RowVersionAsync($"/api/v1/staff/{seat}"),
        });
        (await omitted.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");

        var member = await (await t.OwnerClient.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200);
        member.GetProperty("branchIds").EnumerateArray().Select(b => b.GetGuid()).Should().Equal(t.SecondBranchId);
    }

    [Fact]
    public async Task Restricted_manager_cannot_manage_members_whose_access_reaches_beyond_their_branches()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (manager, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "clinic_manager", [t.SecondBranchId]);
        var (_, unrestrictedSeat, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist");
        var (_, mainSeat, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist", [t.MainBranchId]);
        var receptionist = await t.OwnerClient.RoleIdAsync("receptionist");
        var nurse = await t.OwnerClient.RoleIdAsync("nurse");

        // Narrowing an all-branches member to the manager's branch takes the main branch away: refused.
        var narrow = await manager.PutAsJsonAsync($"/api/v1/staff/{unrestrictedSeat}", new
        {
            roleIds = new[] { receptionist }, branchIds = new[] { t.SecondBranchId }, rowVersion = await manager.RowVersionAsync($"/api/v1/staff/{unrestrictedSeat}"),
        });
        (await narrow.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");

        // Pulling a main-branch member into the manager's branch, or changing their roles: refused.
        var pull = await manager.PutAsJsonAsync($"/api/v1/staff/{mainSeat}", new
        {
            roleIds = new[] { nurse }, branchIds = new[] { t.SecondBranchId }, rowVersion = await manager.RowVersionAsync($"/api/v1/staff/{mainSeat}"),
        });
        (await pull.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");

        // Disabling either of them: refused.
        foreach (var seat in new[] { unrestrictedSeat, mainSeat })
            (await (await manager.PostAsync($"/api/v1/staff/{seat}/disable", null)).ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");

        var unrestricted = await (await t.OwnerClient.GetAsync($"/api/v1/staff/{unrestrictedSeat}")).DataAsync(200);
        unrestricted.GetProperty("branchIds").GetArrayLength().Should().Be(0);
        unrestricted.GetProperty("status").GetString().Should().Be("Active");
        var main = await (await t.OwnerClient.GetAsync($"/api/v1/staff/{mainSeat}")).DataAsync(200);
        main.GetProperty("branchIds").EnumerateArray().Select(b => b.GetGuid()).Should().Equal(t.MainBranchId);
        main.GetProperty("roles").EnumerateArray().Single().GetProperty("id").GetGuid().Should().Be(receptionist);
        main.GetProperty("status").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task Unrestricted_owner_still_assigns_any_branch_or_none()
    {
        var t = await factory.TwoBranchClinicAsync();
        var (_, seat, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist", [t.SecondBranchId]);
        var receptionist = await t.OwnerClient.RoleIdAsync("receptionist");

        var widened = await t.OwnerClient.PutAsJsonAsync($"/api/v1/staff/{seat}", new
        {
            roleIds = new[] { receptionist }, branchIds = Array.Empty<Guid>(), rowVersion = await t.OwnerClient.RowVersionAsync($"/api/v1/staff/{seat}"),
        });
        (await widened.DataAsync(200)).GetProperty("branchIds").GetArrayLength().Should().Be(0);

        var moved = await t.OwnerClient.PutAsJsonAsync($"/api/v1/staff/{seat}", new
        {
            roleIds = new[] { receptionist }, branchIds = new[] { t.MainBranchId }, rowVersion = await t.OwnerClient.RowVersionAsync($"/api/v1/staff/{seat}"),
        });
        (await moved.DataAsync(200)).GetProperty("branchIds").EnumerateArray().Select(b => b.GetGuid()).Should().Equal(t.MainBranchId);
    }

    [Fact]
    public async Task Staff_manager_can_still_change_branches_of_a_member_whose_permissions_they_hold()
    {
        // QA-04's guard applies only when the member holds permissions the actor lacks.
        var t = await factory.TwoBranchClinicAsync();
        var managerRole = await t.OwnerClient.CreateRoleAsync("مدير موظفين", "Staff.View", "Staff.Manage", "Patients.View");
        var readOnly = await t.OwnerClient.CreateRoleAsync("قراءة", "Patients.View");
        var (manager, _, _) = await factory.InviteWithRolesAsync(t.OwnerClient, [managerRole], prefix: "staffmanager");
        var (_, seat, _) = await factory.InviteWithRolesAsync(t.OwnerClient, [readOnly], prefix: "reader");

        var res = await manager.PutAsJsonAsync($"/api/v1/staff/{seat}", new
        {
            roleIds = new[] { readOnly }, branchIds = new[] { t.SecondBranchId }, rowVersion = await manager.RowVersionAsync($"/api/v1/staff/{seat}"),
        });
        (await res.DataAsync(200)).GetProperty("branchIds").EnumerateArray().Select(b => b.GetGuid()).Should().Equal(t.SecondBranchId);
    }
}
