using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Rescheduling is now a one-click front-desk action, so it gets the same double-booking guarantees as booking:
/// whatever runs in parallel (moves into one slot, a move racing a new booking, two moves of the same appointment),
/// exactly one wins and the doctor's calendar never holds two overlapping appointments.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RescheduleConcurrencyTests(ApiFactory factory)
{
    private static async Task<JsonElement> DataAsync(HttpResponseMessage res, int status)
    {
        await res.EnsureStatusAsync(status);
        return (await res.ReadJsonAsync()).GetProperty("data");
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, JsonElement appt, DateTime startUtc) =>
        client.PutAsJsonAsync($"/api/v1/appointments/{appt.GetProperty("id").GetGuid()}/reschedule",
            new { startUtc, rowVersion = appt.GetProperty("rowVersion").GetString() });

    private static async Task<List<(DateTime Start, DateTime End)>> ActiveSlotsAsync(Scenarios.BookableClinic c, int daysAhead)
    {
        var body = await (await c.Owner.Client.GetAsync($"/api/v1/appointments?date={Scenarios.LocalDateIso(daysAhead)}&pageSize=100")).ReadJsonAsync();
        return body.GetProperty("data").EnumerateArray()
            .Where(a => a.GetProperty("status").GetString() is "Scheduled" or "Confirmed" or "CheckedIn")
            .Select(a => (a.GetProperty("startUtc").GetDateTime(), a.GetProperty("endUtc").GetDateTime()))
            .ToList();
    }

    private static void ShouldNotOverlap(List<(DateTime Start, DateTime End)> slots)
    {
        var ordered = slots.OrderBy(s => s.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
            ordered[i].Start.Should().BeOnOrAfter(ordered[i - 1].End, "the doctor's calendar must never hold two overlapping appointments");
    }

    [Fact]
    public async Task Concurrent_moves_of_different_appointments_into_one_slot_yield_exactly_one()
    {
        var c = await factory.BookableClinicAsync();
        var appts = new List<JsonElement>();
        for (var h = 9; h <= 14; h++) appts.Add(await DataAsync(await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(3, h)), 201));
        var target = Scenarios.RiyadhUtc(3, 16);

        var results = await Task.WhenAll(appts.Select(a => MoveAsync(c.Owner.Client, a, target)));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Where(r => r.StatusCode != HttpStatusCode.OK).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict);
        foreach (var r in results.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            (await r.ErrorCodeAsync()).Should().Be("SLOT_TAKEN");
        var slots = await ActiveSlotsAsync(c, 3);
        slots.Should().HaveCount(6, "a refused move leaves the appointment where it was");
        ShouldNotOverlap(slots);
    }

    [Fact]
    public async Task A_move_racing_a_new_booking_for_the_same_slot_yields_exactly_one()
    {
        var c = await factory.BookableClinicAsync();
        var target = Scenarios.RiyadhUtc(4, 12);
        for (var round = 0; round < 3; round++)
        {
            var appt = await DataAsync(await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(4, 9 + round)), 201);
            var slot = target.AddHours(round);
            var results = await Task.WhenAll(MoveAsync(c.Owner.Client, appt, slot), c.Owner.Client.BookAsync(c, slot));
            results.Count(r => (int)r.StatusCode is 200 or 201).Should().Be(1, $"round {round}");
            results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1, $"round {round}");
        }
        ShouldNotOverlap(await ActiveSlotsAsync(c, 4));
    }

    [Fact]
    public async Task Parallel_moves_of_the_same_appointment_with_one_row_version_never_both_succeed()
    {
        var c = await factory.BookableClinicAsync();
        var appt = await DataAsync(await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(5, 9)), 201);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => MoveAsync(c.Owner.Client, appt, Scenarios.RiyadhUtc(5, 11 + i))));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Where(r => r.StatusCode != HttpStatusCode.OK).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict);
        var slots = await ActiveSlotsAsync(c, 5);
        slots.Should().HaveCount(1, "a move never duplicates the appointment");
    }
}
