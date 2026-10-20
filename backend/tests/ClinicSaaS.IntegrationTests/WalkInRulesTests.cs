using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// T-003 / finding F1: a walk-in's start time is set by the server ("now"). A client-supplied StartUtc on a
/// WalkIn create used to skip the working-hours, past-time and overlap rules, so it is now rejected.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WalkInRulesTests(ApiFactory factory)
{
    private static Task<HttpResponseMessage> WalkInAsync(HttpClient client, Scenarios.BookableClinic c, DateTime? startUtc) =>
        client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = c.PatientId, doctorId = c.DoctorId, branchId = c.BranchId, startUtc, source = "WalkIn",
        });

    [Fact]
    public async Task Walk_in_with_a_future_start_time_is_rejected()
    {
        var c = await factory.BookableClinicAsync();
        // 03:00 Riyadh in 30 days: outside working hours and far in the future — previously accepted.
        var res = await WalkInAsync(c.Owner.Client, c, Scenarios.RiyadhUtc(30, 3));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.ReadJsonAsync();
        body.GetProperty("code").GetString().Should().Be("VALIDATION_FAILED");
        body.GetProperty("errors").TryGetProperty("startUtc", out _).Should().BeTrue();
        (await ListAllAsync(c)).Should().Be(0);
    }

    [Fact]
    public async Task Walk_in_with_a_past_start_time_is_rejected()
    {
        var c = await factory.BookableClinicAsync();
        var res = await WalkInAsync(c.Owner.Client, c, DateTime.UtcNow.AddDays(-10));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.ReadJsonAsync();
        body.GetProperty("code").GetString().Should().Be("VALIDATION_FAILED");
        body.GetProperty("errors").TryGetProperty("startUtc", out _).Should().BeTrue();
        (await ListAllAsync(c)).Should().Be(0);
    }

    [Fact]
    public async Task Walk_in_without_a_start_time_starts_now_and_gets_a_queue_token()
    {
        var c = await factory.BookableClinicAsync();
        var before = DateTime.UtcNow;
        var res = await WalkInAsync(c.Owner.Client, c, startUtc: null);
        var after = DateTime.UtcNow;

        await res.EnsureStatusAsync(201);
        var a = (await res.ReadJsonAsync()).GetProperty("data");
        a.GetProperty("tokenNumber").GetString().Should().Be("T-01");
        a.GetProperty("source").GetString().Should().Be("WalkIn");
        var start = a.GetProperty("startUtc").GetDateTime().ToUniversalTime();
        start.Should().BeOnOrAfter(before.AddSeconds(-5)).And.BeOnOrBefore(after.AddSeconds(5));

        // Walk-ins still skip the slot-overlap rule (first come, first served).
        var second = await WalkInAsync(c.Owner.Client, c, startUtc: null);
        await second.EnsureStatusAsync(201);
        (await second.ReadJsonAsync()).GetProperty("data").GetProperty("tokenNumber").GetString().Should().Be("T-02");
    }

    [Fact]
    public async Task Regular_bookings_keep_working_hours_past_and_overlap_rules()
    {
        var c = await factory.BookableClinicAsync();

        await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 10))).EnsureStatusAsync(201);

        var overlap = await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 10));
        overlap.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await overlap.ErrorCodeAsync()).Should().Be("SLOT_TAKEN");

        var outside = await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 3));
        outside.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await outside.ErrorCodeAsync()).Should().Be("OUTSIDE_WORKING_HOURS");

        var past = await c.Owner.Client.BookAsync(c, DateTime.UtcNow.AddHours(-2));
        (await past.ErrorCodeAsync()).Should().Be("SLOT_IN_PAST");

        // A regular booking still requires a start time.
        var missing = await c.Owner.Client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = c.PatientId, doctorId = c.DoctorId, branchId = c.BranchId, source = "Reception",
        });
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.ReadJsonAsync()).GetProperty("errors").TryGetProperty("startUtc", out _).Should().BeTrue();
    }

    private static async Task<int> ListAllAsync(Scenarios.BookableClinic c)
    {
        var body = await (await c.Owner.Client.GetAsync("/api/v1/appointments?pageSize=100")).ReadJsonAsync();
        return body.GetProperty("meta").GetProperty("totalCount").GetInt32();
    }
}
