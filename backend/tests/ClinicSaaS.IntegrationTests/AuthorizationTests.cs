using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ClinicSaaS.Application.Abstractions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>Authentication → tenant → permission → resource ownership, per endpoint.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthorizationTests(ApiFactory factory)
{
    [Fact]
    public async Task Anonymous_requests_get_401_in_the_unified_format()
    {
        var res = await factory.CreateClient().GetAsync("/api/v1/patients");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.ReadJsonAsync();
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("code").GetString().Should().Be("UNAUTHORIZED");
        body.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Tampered_token_is_rejected()
    {
        var a = await factory.RegisterClinicAsync();
        var parts = a.AccessToken.Split('.');
        // Flip a character in the payload: the signature no longer matches.
        var payload = parts[1][..^2] + (parts[1][^2] == 'A' ? 'B' : 'A') + parts[1][^1];
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{parts[0]}.{payload}.{parts[2]}");
        (await client.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Receptionist_sees_demographics_but_not_clinical_data_and_cannot_delete()
    {
        var owner = await factory.RegisterClinicAsync();
        var patient = await owner.Client.CreatePatientAsync();
        var id = patient.GetProperty("id").GetGuid();
        var update = await owner.Client.PutAsJsonAsync($"/api/v1/patients/{id}/medical",
            new { allergies = "بنسلين", rowVersion = patient.GetProperty("rowVersion").GetString() });
        await update.EnsureStatusAsync(200);

        // Owner (has MedicalRecords.View) sees the clinical block.
        var asOwner = await (await owner.Client.GetAsync($"/api/v1/patients/{id}")).ReadJsonAsync();
        asOwner.GetProperty("data").GetProperty("medical").GetProperty("allergies").GetString().Should().Be("بنسلين");

        var receptionist = await AddStaffAsync(owner.TenantId, SystemRoles.Receptionist);

        var asReception = await (await receptionist.GetAsync($"/api/v1/patients/{id}")).ReadJsonAsync();
        asReception.GetProperty("data").TryGetProperty("medical", out _).Should().BeFalse("clinical data needs MedicalRecords.View");

        var delete = await receptionist.DeleteAsync($"/api/v1/patients/{id}");
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await delete.ErrorCodeAsync()).Should().Be("FORBIDDEN");

        var medical = await receptionist.PutAsJsonAsync($"/api/v1/patients/{id}/medical", new { allergies = "x", rowVersion = "AAAAAAAAAAA=" });
        medical.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Denials are audited.
        await factory.WithDbAsync(null, async db =>
            (await db.AuditLogs.CountAsync(l => l.TenantId == owner.TenantId && l.Action == AuditActions.AccessDenied)).Should().BeGreaterThanOrEqualTo(2));
    }

    [Fact]
    public async Task Staff_role_cannot_create_patients()
    {
        var owner = await factory.RegisterClinicAsync();
        var staff = await AddStaffAsync(owner.TenantId, SystemRoles.Staff);
        var res = await staff.PostAsJsonAsync("/api/v1/patients", TestApi.NewPatient());
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staff.GetAsync("/api/v1/patients")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Viewing_a_patient_is_audited_without_clinical_values()
    {
        var owner = await factory.RegisterClinicAsync();
        var id = (await owner.Client.CreatePatientAsync()).GetProperty("id").GetGuid();
        await owner.Client.GetAsync($"/api/v1/patients/{id}");

        await factory.WithDbAsync(null, async db =>
        {
            var log = await db.AuditLogs.SingleAsync(l => l.Action == AuditActions.PatientViewed && l.EntityId == id.ToString());
            log.TenantId.Should().Be(owner.TenantId);
            log.UserId.Should().Be(owner.UserId);
            log.CorrelationId.Should().NotBeNullOrEmpty();
            (log.Details ?? "").Should().NotContain("محمد");
        });
    }

    /// <summary>Creates a user with a single system role in the tenant and returns a logged-in client.</summary>
    private async Task<HttpClient> AddStaffAsync(Guid tenantId, string roleKey)
    {
        var email = TestApi.UniqueEmail(roleKey);
        await factory.WithDbAsync(tenantId, async db =>
        {
            var hasher = factory.Services.GetRequiredService<IPasswordHasher>();
            var user = new User { Email = email, NormalizedEmail = User.Normalize(email), FullName = roleKey, EmailConfirmed = true };
            user.PasswordHash = hasher.Hash(user, TestApi.Password);
            db.Users.Add(user);
            var role = await db.Roles.SingleAsync(r => r.SystemKey == roleKey);
            var membership = new TenantMembership { UserId = user.Id, Status = MembershipStatus.Active };
            membership.Roles.Add(new MembershipRole { RoleId = role.Id });
            db.TenantMemberships.Add(membership);
            await db.SaveChangesAsync();
        });
        return await factory.LoginAsync(email);
    }
}
