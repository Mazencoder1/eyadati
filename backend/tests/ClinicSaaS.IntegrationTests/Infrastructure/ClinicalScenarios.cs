using System.Net.Http.Json;
using System.Text.Json;

namespace ClinicSaaS.IntegrationTests.Infrastructure;

/// <summary>Setups for visits and prescriptions: checked-in appointments, extra branches, custom-role staff.</summary>
public static class ClinicalScenarios
{
    /// <summary>Books a regular appointment (Riyadh <paramref name="hour"/>:00, <paramref name="daysAhead"/> days ahead) and checks it in.</summary>
    public static async Task<Guid> CheckedInAppointmentAsync(this HttpClient client, Scenarios.BookableClinic c, int daysAhead = 1, int hour = 10)
    {
        var booked = await client.BookAsync(c, Scenarios.RiyadhUtc(daysAhead, hour));
        await booked.EnsureStatusAsync(201);
        var a = (await booked.ReadJsonAsync()).GetProperty("data");
        var id = a.GetProperty("id").GetGuid();
        var checkIn = await client.PostAsJsonAsync($"/api/v1/appointments/{id}/check-in", new { rowVersion = a.GetProperty("rowVersion").GetString() });
        await checkIn.EnsureStatusAsync(200);
        return id;
    }

    public static object Clinical(string diagnosis = "التهاب البلعوم الحاد") => new
    {
        symptoms = "ألم في الحلق وحرارة",
        diagnosis,
        diagnosisCode = "j02.9",
        clinicalNotes = "احمرار في البلعوم",
        treatmentPlan = "راحة وسوائل",
        vitals = new { systolicBp = 120, diastolicBp = 80, heartRate = 88, respiratoryRate = 16, temperatureC = 38.2m, oxygenSaturation = 98, weightKg = 72.5m, heightCm = 175.0m },
    };

    public static async Task<JsonElement> RecordVisitAsync(this HttpClient client, Scenarios.BookableClinic c, Guid? appointmentId, object? clinical = null)
    {
        var res = await client.PostAsJsonAsync("/api/v1/visits", new
        {
            patientId = c.PatientId, doctorId = c.DoctorId, branchId = appointmentId is null ? c.BranchId : (Guid?)null, appointmentId,
            clinical, followUpDate = Scenarios.LocalDateIso(14), followUpNotes = "مراجعة بعد أسبوعين",
        });
        await res.EnsureStatusAsync(201);
        return (await res.ReadJsonAsync()).GetProperty("data");
    }

    /// <summary>Records a visit without an appointment (in the clinic's main branch) and returns its id.</summary>
    public static async Task<Guid> VisitIdAsync(this HttpClient client, Scenarios.BookableClinic c) =>
        (await client.RecordVisitAsync(c, appointmentId: null)).GetProperty("id").GetGuid();

    public static object[] RxItems(int count = 2) => Enumerable.Range(1, count).Select(i => (object)new
    {
        medicationName = $"Paracetamol {i}", strength = "500 mg", dosage = "1 tablet", frequency = "3 times daily", route = "oral",
        durationDays = 5, instructions = "بعد الأكل",
    }).ToArray();

    /// <summary>D8: a prescription names only its visit; patient, doctor and branch come from the visit and the caller.</summary>
    public static Task<HttpResponseMessage> IssuePrescriptionAsync(this HttpClient client, Guid visitId, object[]? items = null) =>
        client.PostAsJsonAsync("/api/v1/prescriptions", new { visitId, notes = "مراجعة عند الحاجة", items = items ?? RxItems() });

    public static async Task<Guid> PrescriptionIdAsync(this HttpClient client, Guid visitId)
    {
        var res = await client.IssuePrescriptionAsync(visitId);
        await res.EnsureStatusAsync(201);
        return (await res.ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();
    }

    public static async Task<Guid> AddBranchAsync(this ClinicSession owner, string name = "فرع الشمال")
    {
        var res = await owner.Client.PostAsJsonAsync("/api/v1/branches", new { name, city = "الرياض" });
        await res.EnsureStatusAsync(201);
        return (await res.ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();
    }

    /// <summary>Creates a custom role with exactly <paramref name="permissions"/>, invites a member with it and signs them in.</summary>
    public static async Task<HttpClient> MemberWithPermissionsAsync(this ApiFactory factory, ClinicSession owner, params string[] permissions)
    {
        var role = await owner.Client.PostAsJsonAsync("/api/v1/roles", new { name = "دور " + Guid.NewGuid().ToString("N")[..8], permissions });
        await role.EnsureStatusAsync(201);
        var roleId = (await role.ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();

        var email = TestApi.UniqueEmail("custom");
        var invite = await owner.Client.PostAsJsonAsync("/api/v1/staff", new { fullName = "موظف مخصص", email, roleIds = new[] { roleId } });
        await invite.EnsureStatusAsync(201);
        var token = factory.Emails.TokenFor(email, "/accept-invitation");
        await (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token, newPassword = TestApi.Password }))
            .EnsureStatusAsync(204);
        return await factory.LoginAsync(email);
    }
}
