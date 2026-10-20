using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// T-010 (QA-02): an existing account joins a clinic only through an explicit invitation that its owner accepts while
/// signed in. POST /staff answers identically for registered and unregistered emails, and no foreign profile data is
/// visible before acceptance.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StaffInvitationConsentTests(ApiFactory factory)
{
    [Fact]
    public async Task Invite_response_is_identical_for_registered_and_unregistered_emails()
    {
        var existing = await factory.RegisterClinicAsync();
        await factory.LoginAsync(existing.Email);
        var clinic = await ProfessionalClinicAsync();
        var role = await clinic.Client.RoleIdAsync("receptionist");
        var fresh = TestApi.UniqueEmail("fresh");

        // Typed with odd casing: the response must not echo the stored casing of the real account either.
        var a = await clinic.Client.PostAsJsonAsync("/api/v1/staff", new { fullName = "اسم", email = existing.Email.ToUpperInvariant(), phoneNumber = "0501112233", roleIds = new[] { role } });
        var b = await clinic.Client.PostAsJsonAsync("/api/v1/staff", new { fullName = "اسم", email = fresh.ToUpperInvariant(), phoneNumber = "0501112233", roleIds = new[] { role } });
        a.StatusCode.Should().Be(HttpStatusCode.Created);
        b.StatusCode.Should().Be(HttpStatusCode.Created);

        var da = await a.DataAsync(201);
        var db = await b.DataAsync(201);
        Normalize(da, existing.Email).Should().Be(Normalize(db, fresh), "only the email and the new seat id may differ");
        foreach (var d in new[] { da, db })
        {
            d.GetProperty("status").GetString().Should().Be("Invited");
            d.GetProperty("userId").GetGuid().Should().Be(Guid.Empty, "a global account id (sequential, so it dates the account) is not disclosed");
            d.GetProperty("hasPassword").GetBoolean().Should().BeFalse();
            d.GetProperty("emailConfirmed").GetBoolean().Should().BeFalse();
        }
        da.GetProperty("fullName").GetString().Should().Be(existing.Email.ToLowerInvariant());

        // Same masking in the list and on GET; searching by the account's real name finds nothing.
        var list = await (await clinic.Client.GetAsync("/api/v1/staff?pageSize=100")).DataAsync(200);
        var row = list.EnumerateArray().Single(m => m.GetProperty("membershipId").GetGuid() == da.GetProperty("membershipId").GetGuid());
        Normalize(row, existing.Email).Should().Be(Normalize(da, existing.Email));
        var byName = await (await clinic.Client.GetAsync("/api/v1/staff?search=" + Uri.EscapeDataString("د. مالك"))).DataAsync(200);
        byName.EnumerateArray().Should().OnlyContain(m => m.GetProperty("isOwner").GetBoolean(), "only the prober's own owner may match");
    }

    [Fact]
    public async Task Pending_invitation_gives_no_membership_until_the_signed_in_owner_of_the_account_accepts()
    {
        var victim = await factory.RegisterClinicAsync();
        var clinic = await ProfessionalClinicAsync();
        var seat = (await (await clinic.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "x", email = victim.Email, roleIds = new[] { await clinic.Client.RoleIdAsync("staff") },
        })).DataAsync(201)).GetProperty("membershipId").GetGuid();
        var token = factory.Emails.TokenFor(victim.Email, "/accept-invitation");

        // Before acceptance: login unchanged, no tenant choice, cannot enter or switch into the inviting clinic.
        var login = await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = victim.Email, password = TestApi.Password })).DataAsync(200);
        login.GetProperty("tenant").GetProperty("id").GetGuid().Should().Be(victim.TenantId);
        var victimClient = factory.WithToken(login.GetProperty("accessToken").GetString()!);
        (await (await victimClient.GetAsync("/api/v1/auth/tenants")).DataAsync(200)).GetArrayLength().Should().Be(1);
        (await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = victim.Email, password = TestApi.Password, tenantId = clinic.TenantId }))
            .ErrorCodeAsync()).Should().Be("NO_ACTIVE_MEMBERSHIP");
        (await (await victimClient.PostAsJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = clinic.TenantId })).ErrorCodeAsync())
            .Should().Be("NO_ACTIVE_MEMBERSHIP");

        // The link alone (no sign-in) neither accepts nor resets the existing account's password.
        var anonymous = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token, newPassword = "Hijack-Passw0rd-1" });
        anonymous.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await anonymous.ErrorCodeAsync()).Should().Be("INVITATION_SIGN_IN_REQUIRED");
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = victim.Email, password = "Hijack-Passw0rd-1" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Signed in as somebody else: refused too.
        var other = await factory.RegisterClinicAsync();
        (await (await other.Client.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token })).ErrorCodeAsync()).Should().Be("INVITATION_SIGN_IN_REQUIRED");
        (await (await clinic.Client.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200)).GetProperty("status").GetString().Should().Be("Invited");

        // The account owner, signed in: accepted.
        await (await victimClient.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token })).EnsureStatusAsync(204);
        var member = await (await clinic.Client.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200);
        member.GetProperty("status").GetString().Should().Be("Active");
        member.GetProperty("fullName").GetString().Should().Be("د. مالك", "after acceptance the clinic sees its member");
        var choice = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = victim.Email, password = TestApi.Password });
        (await choice.ErrorCodeAsync()).Should().Be("TENANT_SELECTION_REQUIRED");

        // Single use.
        (await (await victimClient.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token })).ErrorCodeAsync()).Should().Be("INVALID_OR_EXPIRED_TOKEN");
    }

    [Fact]
    public async Task Revoked_invitation_cannot_be_accepted_and_re_enabling_requires_a_new_acceptance()
    {
        var victim = await factory.RegisterClinicAsync();
        var victimClient = await factory.LoginAsync(victim.Email);
        var clinic = await ProfessionalClinicAsync();
        var seat = (await (await clinic.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "x", email = victim.Email, roleIds = new[] { await clinic.Client.RoleIdAsync("staff") },
        })).DataAsync(201)).GetProperty("membershipId").GetGuid();
        var firstToken = factory.Emails.TokenFor(victim.Email, "/accept-invitation");

        // "enable" on a pending seat does not skip the acceptance.
        await (await clinic.Client.PostAsync($"/api/v1/staff/{seat}/enable", null)).EnsureStatusAsync(204);
        (await (await clinic.Client.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200)).GetProperty("status").GetString().Should().Be("Invited");

        // Disabling revokes the pending invitation.
        await (await clinic.Client.PostAsync($"/api/v1/staff/{seat}/disable", null)).EnsureStatusAsync(204);
        (await (await victimClient.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token = firstToken })).ErrorCodeAsync()).Should().Be("INVALID_OR_EXPIRED_TOKEN");

        // Re-enabling sends a new invitation; the seat stays pending until accepted.
        await (await clinic.Client.PostAsync($"/api/v1/staff/{seat}/enable", null)).EnsureStatusAsync(204);
        (await (await clinic.Client.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200)).GetProperty("status").GetString().Should().Be("Invited");
        (await (await victimClient.GetAsync("/api/v1/auth/tenants")).DataAsync(200)).GetArrayLength().Should().Be(1);
        var secondToken = factory.Emails.TokenFor(victim.Email, "/accept-invitation");
        secondToken.Should().NotBe(firstToken);
        await (await victimClient.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token = secondToken })).EnsureStatusAsync(204);
        (await (await clinic.Client.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200)).GetProperty("status").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task An_invitation_token_only_activates_the_seat_it_was_issued_for()
    {
        var victim = await factory.RegisterClinicAsync();
        var victimClient = await factory.LoginAsync(victim.Email);
        var clinicA = await ProfessionalClinicAsync();
        var clinicB = await ProfessionalClinicAsync();
        var seatA = (await (await clinicA.Client.PostAsJsonAsync("/api/v1/staff", new { fullName = "x", email = victim.Email, roleIds = new[] { await clinicA.Client.RoleIdAsync("staff") } })).DataAsync(201))
            .GetProperty("membershipId").GetGuid();
        var tokenA = factory.Emails.TokenFor(victim.Email, "/accept-invitation");
        var seatB = (await (await clinicB.Client.PostAsJsonAsync("/api/v1/staff", new { fullName = "x", email = victim.Email, roleIds = new[] { await clinicB.Client.RoleIdAsync("staff") } })).DataAsync(201))
            .GetProperty("membershipId").GetGuid();

        // Clinic B resending its invitation must not invalidate clinic A's pending link.
        await (await clinicB.Client.PostAsync($"/api/v1/staff/{seatB}/resend-invitation", null)).EnsureStatusAsync(202);
        await (await victimClient.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token = tokenA })).EnsureStatusAsync(204);

        (await (await clinicA.Client.GetAsync($"/api/v1/staff/{seatA}")).DataAsync(200)).GetProperty("status").GetString().Should().Be("Active");
        (await (await clinicB.Client.GetAsync($"/api/v1/staff/{seatB}")).DataAsync(200)).GetProperty("status").GetString().Should().Be("Invited");
    }

    [Fact]
    public async Task New_account_accepts_with_a_password_and_the_seat_becomes_active()
    {
        var clinic = await ProfessionalClinicAsync();
        var email = TestApi.UniqueEmail("newcomer");
        var seat = (await (await clinic.Client.PostAsJsonAsync("/api/v1/staff", new { fullName = "موظف جديد", email, roleIds = new[] { await clinic.Client.RoleIdAsync("staff") } })).DataAsync(201))
            .GetProperty("membershipId").GetGuid();
        var token = factory.Emails.TokenFor(email, "/accept-invitation");

        // A new account must choose a password.
        var noPassword = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token });
        noPassword.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token, newPassword = TestApi.Password })).EnsureStatusAsync(204);
        var member = await (await clinic.Client.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200);
        member.GetProperty("status").GetString().Should().Be("Active");
        member.GetProperty("fullName").GetString().Should().Be("موظف جديد");
        await factory.LoginAsync(email);
    }

    [Fact]
    public async Task QA12_former_member_who_accepted_stays_visible_after_being_disabled()
    {
        // The QA-12 fix masks only seats whose account never joined this clinic; a real former member keeps their profile.
        var clinic = await ProfessionalClinicAsync();
        var (_, seat, email) = await factory.InviteAndAcceptAsync(clinic, "staff");
        await (await clinic.Client.PostAsync($"/api/v1/staff/{seat}/disable", null)).EnsureStatusAsync(204);

        var member = await (await clinic.Client.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200);
        member.GetProperty("status").GetString().Should().Be("Disabled");
        member.GetProperty("fullName").GetString().Should().Be("موظف staff");
        member.GetProperty("userId").GetGuid().Should().NotBe(Guid.Empty);
        member.GetProperty("hasPassword").GetBoolean().Should().BeTrue();
        member.GetProperty("email").GetString().Should().Be(email);

        var byName = await (await clinic.Client.GetAsync("/api/v1/staff?status=Disabled&search=" + Uri.EscapeDataString("موظف staff"))).DataAsync(200);
        byName.EnumerateArray().Should().Contain(m => m.GetProperty("membershipId").GetGuid() == seat);
    }

    [Fact]
    public async Task QA12_membership_at_another_clinic_never_unmasks_a_disabled_pending_seat()
    {
        // The evidence of "joined" is scoped to the clinic: an account that accepted and signs in at clinic A stays
        // masked at clinic B, which invited it and disabled the seat before acceptance — on GET, in the list and in search.
        var victim = await factory.RegisterClinicAsync();
        var clinicA = await ProfessionalClinicAsync();
        await factory.AddExistingAccountAsync(clinicA.Client, victim.Email, await clinicA.Client.RoleIdAsync("staff"));
        // The account also signs in at clinic A (login with a clinic choice, then a switch back to it).
        var signedIn = await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = victim.Email, password = TestApi.Password, tenantId = clinicA.TenantId }))
            .DataAsync(200);
        await (await factory.WithToken(signedIn.GetProperty("accessToken").GetString()!)
            .PostAsJsonAsync("/api/v1/auth/switch-tenant", new { tenantId = clinicA.TenantId })).EnsureStatusAsync(200);

        var clinicB = await ProfessionalClinicAsync();
        var seatB = (await (await clinicB.Client.PostAsJsonAsync("/api/v1/staff", new
        {
            fullName = "x", email = victim.Email, roleIds = new[] { await clinicB.Client.RoleIdAsync("staff") },
        })).DataAsync(201)).GetProperty("membershipId").GetGuid();
        await (await clinicB.Client.PostAsync($"/api/v1/staff/{seatB}/disable", null)).EnsureStatusAsync(204);

        var member = await (await clinicB.Client.GetAsync($"/api/v1/staff/{seatB}")).DataAsync(200);
        member.GetProperty("status").GetString().Should().Be("Disabled");
        member.GetProperty("fullName").GetString().Should().Be(victim.Email.ToLowerInvariant());
        member.GetProperty("userId").GetGuid().Should().Be(Guid.Empty);
        member.GetProperty("hasPassword").GetBoolean().Should().BeFalse();
        member.GetProperty("emailConfirmed").GetBoolean().Should().BeFalse();
        (member.TryGetProperty("phoneNumber", out var phone) && phone.ValueKind != JsonValueKind.Null).Should().BeFalse();
        (member.TryGetProperty("lastLoginAtUtc", out var last) && last.ValueKind != JsonValueKind.Null).Should().BeFalse();

        var row = (await (await clinicB.Client.GetAsync("/api/v1/staff?status=Disabled&pageSize=100")).DataAsync(200)).EnumerateArray()
            .Single(m => m.GetProperty("membershipId").GetGuid() == seatB);
        Normalize(row, victim.Email).Should().Be(Normalize(member, victim.Email));
        (await (await clinicB.Client.GetAsync("/api/v1/staff?search=" + Uri.EscapeDataString("د. مالك"))).DataAsync(200))
            .EnumerateArray().Should().OnlyContain(m => m.GetProperty("isOwner").GetBoolean(), "only clinic B's own owner may match");

        // Clinic A, where the account did join, still sees its member normally.
        (await (await clinicA.Client.GetAsync("/api/v1/staff?search=" + Uri.EscapeDataString("د. مالك"))).DataAsync(200))
            .EnumerateArray().Should().Contain(m => !m.GetProperty("isOwner").GetBoolean());
    }

    // ------------------------------------------------------------------ helpers

    private async Task<ClinicSession> ProfessionalClinicAsync()
    {
        var owner = await factory.RegisterClinicAsync();
        await factory.UsePlanAsync(owner.TenantId, "professional");
        return owner;
    }

    /// <summary>The response with the per-request values (seat id, email echoes) blanked out.</summary>
    private static string Normalize(JsonElement member, string email)
    {
        var lower = email.ToLowerInvariant();
        var fields = member.EnumerateObject()
            .Where(p => p.Name is not ("membershipId" or "rowVersion"))
            .Select(p => $"{p.Name}={(p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.ToLowerInvariant() == lower ? "<email>" : p.Value.GetRawText())}");
        return string.Join("|", fields);
    }
}
