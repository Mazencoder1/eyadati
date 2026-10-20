using System.Net.Http.Json;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Infrastructure.Persistence;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// The single most important guarantee of the product: Tenant A can never read or change Tenant B's data —
/// verified at the API, the data-access layer and the database itself.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TenantIsolationTests(ApiFactory factory)
{
    // ------------------------------------------------------------------ API layer

    [Fact]
    public async Task Tenant_A_cannot_read_update_or_delete_tenant_B_patient()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await factory.RegisterClinicAsync();
        var patientOfB = await b.Client.CreatePatientAsync();
        var id = patientOfB.GetProperty("id").GetGuid();
        var rowVersion = patientOfB.GetProperty("rowVersion").GetString();

        var get = await a.Client.GetAsync($"/api/v1/patients/{id}");
        get.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        (await get.ErrorCodeAsync()).Should().Be("PATIENT_NOT_FOUND");

        var put = await a.Client.PutAsJsonAsync($"/api/v1/patients/{id}", new
        {
            fullName = "مخترق", mobileNumber = "0500000000", gender = "Male", identityType = "None", rowVersion,
        });
        put.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);

        var medical = await a.Client.PutAsJsonAsync($"/api/v1/patients/{id}/medical", new { allergies = "x", rowVersion });
        medical.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);

        var delete = await a.Client.DeleteAsync($"/api/v1/patients/{id}");
        delete.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);

        // B's record is untouched and still visible to B.
        var stillThere = await (await b.Client.GetAsync($"/api/v1/patients/{id}")).ReadJsonAsync();
        stillThere.GetProperty("data").GetProperty("fullName").GetString().Should().Be("محمد عبدالله");
    }

    [Fact]
    public async Task Lists_and_searches_only_return_own_tenant_rows()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await factory.RegisterClinicAsync();
        await b.Client.CreatePatientAsync(TestApi.NewPatient(mobile: "0559998887"));
        await a.Client.CreatePatientAsync(TestApi.NewPatient(mobile: "0551112223"));

        var list = await (await a.Client.GetAsync("/api/v1/patients?pageSize=100")).ReadJsonAsync();
        list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(1);

        var search = await (await a.Client.GetAsync("/api/v1/patients?search=0559998887")).ReadJsonAsync();
        search.GetProperty("data").GetArrayLength().Should().Be(0);

        var dupes = await (await a.Client.GetAsync("/api/v1/patients/duplicates?mobile=0559998887")).ReadJsonAsync();
        dupes.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Cannot_reference_another_tenants_branch()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await factory.RegisterClinicAsync();
        Guid branchOfB = default;
        await factory.WithDbAsync(b.TenantId, async db => branchOfB = await db.Branches.Select(x => x.Id).SingleAsync());

        var res = await a.Client.PostAsJsonAsync("/api/v1/patients", new
        {
            fullName = "مريض", mobileNumber = "0551234567", gender = "Female", identityType = "None", registeredBranchId = branchOfB,
        });
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ data-access layer

    [Fact]
    public async Task Query_filter_returns_nothing_without_a_tenant_scope()
    {
        var a = await factory.RegisterClinicAsync();
        await a.Client.CreatePatientAsync();

        await factory.WithDbAsync(null, async db => (await db.Patients.CountAsync()).Should().Be(0));
        await factory.WithDbAsync(a.TenantId, async db => (await db.Patients.CountAsync()).Should().Be(1));
    }

    [Fact]
    public async Task Save_guard_blocks_writes_to_another_tenants_rows()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await factory.RegisterClinicAsync();
        var idOfB = (await b.Client.CreatePatientAsync()).GetProperty("id").GetGuid();

        // Load B's row bypassing the filter (as a buggy query might), then try to save it while scoped to A.
        await factory.WithDbAsync(a.TenantId, async db =>
        {
            var leaked = await db.Patients.IgnoreQueryFilters().SingleAsync(p => p.Id == idOfB);
            leaked.FullName = "tampered";
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

        // Inserting a row stamped with another tenant id is refused too.
        await factory.WithDbAsync(a.TenantId, async db =>
        {
            db.Patients.Add(new Patient { TenantId = b.TenantId, MedicalRecordNumber = "X-1", FullName = "x", MobileNumber = "+966500000000" });
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });

        // Writing tenant-owned rows with no tenant scope at all is refused.
        await factory.WithDbAsync(null, async db =>
        {
            db.Patients.Add(new Patient { MedicalRecordNumber = "X-2", FullName = "x", MobileNumber = "+966500000000" });
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<TenantIsolationViolationException>();
        });
    }

    // ------------------------------------------------------------------ database layer

    [Fact]
    public async Task Database_rejects_cross_tenant_foreign_keys_even_with_raw_sql()
    {
        var a = await factory.RegisterClinicAsync();
        var b = await factory.RegisterClinicAsync();
        Guid branchOfB = default;
        await factory.WithDbAsync(b.TenantId, async db => branchOfB = await db.Branches.Select(x => x.Id).SingleAsync());

        // Bypass EF entirely: a patient of tenant A pointing at tenant B's branch violates the composite FK.
        await factory.WithDbAsync(a.TenantId, async db =>
        {
            var insert = () => db.Database.ExecuteSqlAsync($"""
                INSERT INTO Patients (Id, TenantId, MedicalRecordNumber, FullName, MobileNumber, IdentityType, Gender, RegisteredBranchId, CreatedAtUtc)
                VALUES (NEWID(), {a.TenantId}, 'RAW-1', N'x', '+966500000000', 'None', 'Male', {branchOfB}, SYSUTCDATETIME())
                """);
            await insert.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>().Where(e => e.Number == 547);
        });
    }
}
