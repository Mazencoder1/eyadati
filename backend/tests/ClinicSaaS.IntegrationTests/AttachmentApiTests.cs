using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Attachments;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Infrastructure.Persistence;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// P1-02: patient attachments — upload validation, authorization, tenant/branch/own-doctor isolation, the scan
/// quarantine (no scanner exists: files stay Pending; tests move the state through the domain transitions only to
/// prove the download gate), path safety, and storage/metadata failure handling with cleanup.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AttachmentApiTests(ApiFactory factory)
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 1, 2, 3];
    private static readonly Regex StorageKeyFile = new("^[0-9a-f]{32}/[0-9]{4}/[0-9]{2}/[0-9a-f]{32}$");

    // ------------------------------------------------------------------ happy path + quarantine

    [Fact]
    public async Task Upload_is_pending_and_quarantined_then_downloadable_only_after_a_clean_scan()
    {
        var c = await factory.BookableClinicAsync();
        var res = await UploadAsync(c.Owner.Client, c.PatientId, Pdf, "تقرير مختبر.pdf", "application/pdf", category: "LabResult", description: "CBC");
        var body = await res.ReadJsonAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        res.Headers.Location!.ToString().Should().EndWith($"/api/v1/attachments/{body.GetProperty("data").GetProperty("id").GetGuid()}");
        var a = body.GetProperty("data");
        a.GetProperty("fileName").GetString().Should().Be("تقرير مختبر.pdf");
        a.GetProperty("contentType").GetString().Should().Be("application/pdf");
        a.GetProperty("sizeBytes").GetInt64().Should().Be(Pdf.Length);
        a.GetProperty("category").GetString().Should().Be("LabResult");
        a.GetProperty("scanStatus").GetString().Should().Be("Pending");
        a.GetProperty("canDownload").GetBoolean().Should().BeFalse();
        a.GetProperty("createdByUserId").GetGuid().Should().Be(c.Owner.UserId);
        var raw = body.GetRawText();
        raw.Should().NotContainAny("storageKey", "sha256", factory.StorageRoot);

        var id = a.GetProperty("id").GetGuid();
        (await c.Owner.Client.GetAsync($"/api/v1/attachments/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var blocked = await c.Owner.Client.GetAsync($"/api/v1/attachments/{id}/content");
        blocked.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var err = await blocked.ReadJsonAsync();
        err.GetProperty("code").GetString().Should().Be("FILE_NOT_AVAILABLE");
        err.GetProperty("details").GetProperty("scanStatus").GetString().Should().Be("Pending");

        await MarkScanAsync(c.Owner.TenantId, id, FileScanStatus.Scanning, FileScanStatus.Clean);
        var ok = await c.Owner.Client.GetAsync($"/api/v1/attachments/{id}/content");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.Content.ReadAsByteArrayAsync()).Should().Equal(Pdf);
        ok.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        ok.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        ok.Content.Headers.ContentDisposition.FileNameStar.Should().Be("تقرير مختبر.pdf");
        ok.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");

        // Audit: who/what/when, never the file name or description.
        await factory.WithDbAsync(null, async db =>
        {
            var logs = await db.AuditLogs.Where(l => l.EntityId == id.ToString()).ToListAsync();
            logs.Select(l => l.Action).Should().Contain([AuditActions.FileUploaded, AuditActions.FileDownloaded]);
            logs.Should().OnlyContain(l => l.TenantId == c.Owner.TenantId && !(l.Details ?? "").Contains("تقرير") && !(l.Details ?? "").Contains("CBC"));
        });
    }

    [Theory]
    [InlineData("scan.jpg", "image/jpeg")]
    [InlineData("scan.JPEG", "image/jpeg")]
    [InlineData("xray.png", "image/png")]
    public async Task Jpeg_and_png_are_accepted(string fileName, string contentType)
    {
        var c = await factory.BookableClinicAsync();
        var bytes = contentType == "image/png" ? Png : Jpeg;
        var a = await (await UploadAsync(c.Owner.Client, c.PatientId, bytes, fileName, contentType)).DataAsync(201);
        a.GetProperty("contentType").GetString().Should().Be(contentType);
    }

    [Theory]
    [InlineData(new[] { FileScanStatus.Pending }, "Pending")]
    [InlineData(new[] { FileScanStatus.Scanning }, "Scanning")]
    [InlineData(new[] { FileScanStatus.Failed }, "Failed")]
    [InlineData(new[] { FileScanStatus.Scanning, FileScanStatus.Rejected }, "Rejected")]
    [InlineData(new[] { FileScanStatus.Scanning, FileScanStatus.Failed }, "Failed")]
    public async Task Files_that_are_not_scanned_clean_are_never_downloadable(FileScanStatus[] path, string expected)
    {
        var c = await factory.BookableClinicAsync();
        var id = await UploadedIdAsync(c.Owner.Client, c.PatientId);
        await MarkScanAsync(c.Owner.TenantId, id, path);

        var res = await c.Owner.Client.GetAsync($"/api/v1/attachments/{id}/content");
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.ReadJsonAsync()).GetProperty("details").GetProperty("scanStatus").GetString().Should().Be(expected);
        var meta = await (await c.Owner.Client.GetAsync($"/api/v1/attachments/{id}")).DataAsync(200);
        meta.GetProperty("scanStatus").GetString().Should().Be(expected);
        meta.GetProperty("canDownload").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Soft_deleted_attachment_is_not_found()
    {
        var c = await factory.BookableClinicAsync();
        var id = await UploadedIdAsync(c.Owner.Client, c.PatientId);
        await MarkScanAsync(c.Owner.TenantId, id, FileScanStatus.Scanning, FileScanStatus.Clean);
        await factory.WithDbAsync(c.Owner.TenantId, async db =>
        {
            var a = await db.Set<FileAttachment>().SingleAsync(x => x.Id == id);
            a.IsDeleted = true;
            await db.SaveChangesAsync();
        });
        (await c.Owner.Client.GetAsync($"/api/v1/attachments/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await c.Owner.Client.GetAsync($"/api/v1/attachments/{id}/content")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var list = await (await c.Owner.Client.GetAsync($"/api/v1/patients/{c.PatientId}/attachments")).ReadJsonAsync();
        list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    // ------------------------------------------------------------------ validation

    [Theory]
    [InlineData("tool.exe", "application/octet-stream", "MZ")]
    [InlineData("report.pdf.exe", "application/pdf", "PDF")]
    [InlineData("page.html", "text/html", "PDF")]
    [InlineData("image.gif", "image/gif", "GIF")]
    [InlineData("vector.svg", "image/svg+xml", "SVG")]
    [InlineData("photo.webp", "image/webp", "WEBP")]
    [InlineData("noextension", "application/pdf", "PDF")]
    [InlineData("spoofed.png", "application/pdf", "PNG")]   // declared MIME disagrees with the signature
    [InlineData("spoofed.pdf", "image/png", "PNG")]         // extension disagrees with the signature
    [InlineData("fake.pdf", "application/pdf", "TEXT")]     // right name and MIME, wrong signature
    [InlineData("fake.jpg", "image/jpeg", "TEXT")]
    [InlineData("missing-mime.pdf", "", "PDF")]
    public async Task Disallowed_or_spoofed_files_are_rejected(string fileName, string contentType, string kind)
    {
        var c = await factory.BookableClinicAsync();
        byte[] bytes = kind switch
        {
            "MZ" => [0x4D, 0x5A, 0x90, 0x00, 1, 2, 3],
            "PDF" => Pdf,
            "PNG" => Png,
            "GIF" => Encoding.ASCII.GetBytes("GIF89a......"),
            "SVG" => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"),
            "WEBP" => Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 "),
            _ => Encoding.UTF8.GetBytes("just some text, not a document"),
        };
        var res = await UploadAsync(c.Owner.Client, c.PatientId, bytes, fileName, contentType);
        await ExpectFieldErrorAsync(res, "file");
        await ExpectNothingStoredAsync(c);
    }

    [Fact]
    public async Task Empty_missing_and_non_multipart_uploads_are_rejected()
    {
        var c = await factory.BookableClinicAsync();
        await ExpectFieldErrorAsync(await UploadAsync(c.Owner.Client, c.PatientId, [], "empty.pdf", "application/pdf"), "file");

        using var noFile = new MultipartFormDataContent { { new StringContent("Other"), "category" } };
        await ExpectFieldErrorAsync(await c.Owner.Client.PostAsync($"/api/v1/patients/{c.PatientId}/attachments", noFile), "file");

        var json = await c.Owner.Client.PostAsJsonAsync($"/api/v1/patients/{c.PatientId}/attachments", new { file = Convert.ToBase64String(Pdf) });
        await ExpectFieldErrorAsync(json, "file");

        using var two = new MultipartFormDataContent { FilePart(Pdf, "a.pdf", "application/pdf"), FilePart(Pdf, "b.pdf", "application/pdf") };
        await ExpectFieldErrorAsync(await c.Owner.Client.PostAsync($"/api/v1/patients/{c.PatientId}/attachments", two), "file");
        await ExpectNothingStoredAsync(c);
    }

    [Theory]
    [InlineData("category", "Malware")]
    [InlineData("category", "3")]
    [InlineData("visitId", "not-a-guid")]
    public async Task Invalid_form_fields_are_rejected(string field, string value)
    {
        var c = await factory.BookableClinicAsync();
        var res = await UploadAsync(c.Owner.Client, c.PatientId, Pdf, "a.pdf", "application/pdf", extra: new() { [field] = value });
        await ExpectFieldErrorAsync(res, field);
    }

    [Fact]
    public async Task Description_over_500_characters_is_rejected()
    {
        var c = await factory.BookableClinicAsync();
        await ExpectFieldErrorAsync(await UploadAsync(c.Owner.Client, c.PatientId, Pdf, "a.pdf", "application/pdf", description: new string('x', 501)), "description");
    }

    [Fact]
    public async Task File_over_10_MB_is_rejected_by_the_application_limit()
    {
        var c = await factory.BookableClinicAsync();
        var bytes = new byte[AttachmentService.MaxFileBytes + 1];
        Pdf.CopyTo(bytes, 0);
        var res = await UploadAsync(c.Owner.Client, c.PatientId, bytes, "big.pdf", "application/pdf");
        await ExpectFieldErrorAsync(res, "file");

        var exactly = new byte[AttachmentService.MaxFileBytes];
        Pdf.CopyTo(exactly, 0);
        (await UploadAsync(c.Owner.Client, c.PatientId, exactly, "max.pdf", "application/pdf")).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Request_far_over_the_limit_is_413_and_stores_nothing()
    {
        var c = await factory.BookableClinicAsync();
        var bytes = new byte[12 * 1024 * 1024];
        Pdf.CopyTo(bytes, 0);
        var res = await UploadAsync(c.Owner.Client, c.PatientId, bytes, "huge.pdf", "application/pdf");
        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, await res.Content.ReadAsStringAsync());
        (await res.ErrorCodeAsync()).Should().Be("PAYLOAD_TOO_LARGE");
        await ExpectNothingStoredAsync(c);
    }

    // ------------------------------------------------------------------ authentication / permissions / IDs

    [Fact]
    public async Task Anonymous_requests_are_401()
    {
        var c = await factory.BookableClinicAsync();
        var id = await UploadedIdAsync(c.Owner.Client, c.PatientId);
        var anon = factory.CreateClient();
        (await UploadAsync(anon, c.PatientId, Pdf, "a.pdf", "application/pdf")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync($"/api/v1/patients/{c.PatientId}/attachments")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync($"/api/v1/attachments/{id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync($"/api/v1/attachments/{id}/content")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Role_templates_gate_every_endpoint()
    {
        var c = await factory.BookableClinicAsync();
        var id = await UploadedIdAsync(c.Owner.Client, c.PatientId);

        // Receptionist template: no Files.* and no MedicalRecords.* (data minimization) ⇒ 403 everywhere.
        var (reception, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "receptionist");
        (await UploadAsync(reception, c.PatientId, Pdf, "a.pdf", "application/pdf")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await ExpectReadsAsync(reception, c.PatientId, id, HttpStatusCode.Forbidden);

        // Upload alone does not grant reading.
        var uploader = await factory.MemberWithPermissionsAsync(c.Owner, "Files.Upload");
        (await UploadAsync(uploader, c.PatientId, Pdf, "a.pdf", "application/pdf")).StatusCode.Should().Be(HttpStatusCode.Created);
        await ExpectReadsAsync(uploader, c.PatientId, id, HttpStatusCode.Forbidden);

        // Nurse template carries Files.View + Files.Upload + MedicalRecords.View and no doctor profile.
        var (nurse, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "nurse");
        (await UploadAsync(nurse, c.PatientId, Png, "wound.png", "image/png")).StatusCode.Should().Be(HttpStatusCode.Created);
        await ExpectReadsAsync(nurse, c.PatientId, id, visible: true);
    }

    /// <summary>Decision A/B: every read (list, metadata, download) needs Files.View AND MedicalRecords.View.</summary>
    [Theory]
    [InlineData(new[] { "Files.View" }, false)]
    [InlineData(new[] { "MedicalRecords.View" }, false)]
    [InlineData(new[] { "Files.View", "Files.Upload", "Patients.View" }, false)]
    [InlineData(new[] { "MedicalRecords.View", "MedicalRecords.Edit", "Files.Upload", "Patients.View" }, false)]
    [InlineData(new[] { "Files.View", "MedicalRecords.View" }, true)]
    public async Task Reads_require_both_files_view_and_medical_records_view(string[] permissions, bool allowed)
    {
        var c = await factory.BookableClinicAsync();
        var id = await UploadedIdAsync(c.Owner.Client, c.PatientId);
        var member = await factory.MemberWithPermissionsAsync(c.Owner, permissions);
        if (allowed) await ExpectReadsAsync(member, c.PatientId, id, visible: true);
        else await ExpectReadsAsync(member, c.PatientId, id, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Missing_forged_and_malformed_ids_are_404()
    {
        var c = await factory.BookableClinicAsync();
        var random = Guid.NewGuid();
        (await c.Owner.Client.GetAsync($"/api/v1/attachments/{random}")).Should().Match<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.NotFound);
        (await (await c.Owner.Client.GetAsync($"/api/v1/attachments/{random}")).ErrorCodeAsync()).Should().Be("FILE_NOT_FOUND");
        (await (await c.Owner.Client.GetAsync($"/api/v1/attachments/{random}/content")).ErrorCodeAsync()).Should().Be("FILE_NOT_FOUND");
        (await (await UploadAsync(c.Owner.Client, random, Pdf, "a.pdf", "application/pdf")).ErrorCodeAsync()).Should().Be("PATIENT_NOT_FOUND");
        (await (await c.Owner.Client.GetAsync($"/api/v1/patients/{random}/attachments")).ErrorCodeAsync()).Should().Be("PATIENT_NOT_FOUND");
        (await c.Owner.Client.GetAsync("/api/v1/attachments/../../etc/passwd")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await c.Owner.Client.GetAsync("/api/v1/attachments/not-a-guid/content")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var visit = await UploadAsync(c.Owner.Client, c.PatientId, Pdf, "a.pdf", "application/pdf", extra: new() { ["visitId"] = random.ToString() });
        (await visit.ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
        await ExpectNothingStoredAsync(c);
    }

    // ------------------------------------------------------------------ isolation

    [Fact]
    public async Task Another_clinic_cannot_read_list_download_or_attach_to_foreign_records()
    {
        var a = await factory.BookableClinicAsync();
        var visit = await a.Owner.Client.RecordVisitAsync(a, null);
        var visitId = visit.GetProperty("id").GetGuid();
        var id = await UploadedIdAsync(a.Owner.Client, a.PatientId, visitId);
        await MarkScanAsync(a.Owner.TenantId, id, FileScanStatus.Scanning, FileScanStatus.Clean);

        var b = await factory.BookableClinicAsync();
        (await (await b.Owner.Client.GetAsync($"/api/v1/attachments/{id}")).ErrorCodeAsync()).Should().Be("FILE_NOT_FOUND");
        (await (await b.Owner.Client.GetAsync($"/api/v1/attachments/{id}/content")).ErrorCodeAsync()).Should().Be("FILE_NOT_FOUND");
        (await (await b.Owner.Client.GetAsync($"/api/v1/patients/{a.PatientId}/attachments")).ErrorCodeAsync()).Should().Be("PATIENT_NOT_FOUND");
        (await (await UploadAsync(b.Owner.Client, a.PatientId, Pdf, "x.pdf", "application/pdf")).ErrorCodeAsync()).Should().Be("PATIENT_NOT_FOUND");
        // B's own patient, A's visit: the visit is not found in B.
        var forged = await UploadAsync(b.Owner.Client, b.PatientId, Pdf, "x.pdf", "application/pdf", extra: new() { ["visitId"] = visitId.ToString() });
        (await forged.ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
        // Paging/filters never leak A's rows into B.
        var bList = await (await b.Owner.Client.GetAsync($"/api/v1/patients/{b.PatientId}/attachments?visitId={visitId}&pageSize=100")).ReadJsonAsync();
        bList.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0);
        await ExpectNothingStoredAsync(b);

        // A still sees its own file.
        var aList = await (await a.Owner.Client.GetAsync($"/api/v1/patients/{a.PatientId}/attachments?visitId={visitId}")).ReadJsonAsync();
        aList.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Should().Equal(id);
    }

    [Fact]
    public async Task Visit_must_belong_to_the_same_patient()
    {
        var c = await factory.BookableClinicAsync();
        var visitId = (await c.Owner.Client.RecordVisitAsync(c, null)).GetProperty("id").GetGuid();
        var other = (await c.Owner.Client.CreatePatientAsync(TestApi.NewPatient(mobile: "0507654321"))).GetProperty("id").GetGuid();
        var res = await UploadAsync(c.Owner.Client, other, Pdf, "a.pdf", "application/pdf", extra: new() { ["visitId"] = visitId.ToString() });
        await ExpectFieldErrorAsync(res, "visitId");
    }

    [Fact]
    public async Task Branch_restricted_member_sees_and_attaches_only_within_own_branches()
    {
        var t = await factory.TwoBranchClinicAsync();
        var mainVisit = await VisitAtAsync(t.OwnerClient, t, t.MainBranchId);
        var otherVisit = await VisitAtAsync(t.OwnerClient, t, t.SecondBranchId);
        var onMain = await UploadedIdAsync(t.OwnerClient, t.PatientId, mainVisit);
        var onOther = await UploadedIdAsync(t.OwnerClient, t.PatientId, otherVisit);
        var unlinked = await UploadedIdAsync(t.OwnerClient, t.PatientId);
        foreach (var id in new[] { onMain, onOther, unlinked })
            await MarkScanAsync(t.Owner.TenantId, id, FileScanStatus.Scanning, FileScanStatus.Clean);

        var role = await t.OwnerClient.CreateRoleAsync("ملفات الفرع", "Files.View", "MedicalRecords.View", "Files.Upload", "Patients.View");
        var (member, _, _) = await factory.InviteWithRolesAsync(t.OwnerClient, [role], [t.MainBranchId], "branchfiles");

        var list = await (await member.GetAsync($"/api/v1/patients/{t.PatientId}/attachments?pageSize=100")).ReadJsonAsync();
        list.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Should().BeEquivalentTo([onMain, unlinked]);
        list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(2);
        (await member.GetAsync($"/api/v1/attachments/{onOther}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await member.GetAsync($"/api/v1/attachments/{onOther}/content")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await member.GetAsync($"/api/v1/attachments/{onMain}/content")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await member.GetAsync($"/api/v1/attachments/{unlinked}/content")).StatusCode.Should().Be(HttpStatusCode.OK);

        var foreignVisit = await UploadAsync(member, t.PatientId, Pdf, "a.pdf", "application/pdf", extra: new() { ["visitId"] = otherVisit.ToString() });
        (await foreignVisit.ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
        (await UploadAsync(member, t.PatientId, Pdf, "a.pdf", "application/pdf", extra: new() { ["visitId"] = mainVisit.ToString() }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // The unrestricted owner still sees everything.
        var all = await (await t.OwnerClient.GetAsync($"/api/v1/patients/{t.PatientId}/attachments?pageSize=100")).ReadJsonAsync();
        all.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task Own_records_doctor_sees_files_of_own_visits_and_only_unlinked_files_they_uploaded()
    {
        var c = await factory.BookableClinicAsync();
        var ownerVisit = (await c.Owner.Client.RecordVisitAsync(c, null)).GetProperty("id").GetGuid();
        var (doctor, doctorId) = await OwnRecordsDoctorAsync(c);
        var ownVisitRes = await doctor.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId, branchId = c.BranchId });
        var ownVisit = (await ownVisitRes.DataAsync(201)).GetProperty("id").GetGuid();

        var onOtherDoctorsVisit = await UploadedIdAsync(c.Owner.Client, c.PatientId, ownerVisit);
        var onOwnVisitByOwner = await UploadedIdAsync(c.Owner.Client, c.PatientId, ownVisit);
        var unlinkedByOwner = await UploadedIdAsync(c.Owner.Client, c.PatientId);
        var (nurse, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "nurse");
        var unlinkedByNurse = await UploadedIdAsync(nurse, c.PatientId);
        var unlinkedBySelf = await UploadedIdAsync(doctor, c.PatientId);

        // Linked files keep the visit rule (own visits, whoever uploaded); unlinked files only if uploaded by the doctor.
        (await ListIdsAsync(doctor, c.PatientId)).Should().BeEquivalentTo([onOwnVisitByOwner, unlinkedBySelf]);
        foreach (var hidden in new[] { onOtherDoctorsVisit, unlinkedByOwner, unlinkedByNurse })
            await ExpectReadsAsync(doctor, c.PatientId, hidden, visible: false);
        foreach (var shown in new[] { onOwnVisitByOwner, unlinkedBySelf })
            await ExpectReadsAsync(doctor, c.PatientId, shown, visible: true);

        var linkToOther = await UploadAsync(doctor, c.PatientId, Pdf, "a.pdf", "application/pdf", extra: new() { ["visitId"] = ownerVisit.ToString() });
        (await linkToOther.ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");

        // The rule is specific to own-records doctors: the owner (ViewAllDoctors) and the nurse (no doctor profile) see all.
        (await ListIdsAsync(c.Owner.Client, c.PatientId)).Should().HaveCount(5);
        (await ListIdsAsync(nurse, c.PatientId)).Should().HaveCount(5);
    }

    [Fact]
    public async Task Categories_are_informational_and_never_change_authorization()
    {
        var c = await factory.BookableClinicAsync();
        string[] categories = ["Identity", "Insurance", "LabResult", "Imaging", "Report", "Prescription", "Other"];
        var byOwner = new List<Guid>();
        foreach (var category in categories)
        {
            var res = await UploadAsync(c.Owner.Client, c.PatientId, Pdf, $"{category}.pdf", "application/pdf", category: category);
            byOwner.Add((await res.DataAsync(201)).GetProperty("id").GetGuid());
        }

        var both = await factory.MemberWithPermissionsAsync(c.Owner, "Files.View", "MedicalRecords.View");
        var filesOnly = await factory.MemberWithPermissionsAsync(c.Owner, "Files.View");
        var (doctor, _) = await OwnRecordsDoctorAsync(c);
        (await ListIdsAsync(both, c.PatientId)).Should().BeEquivalentTo(byOwner);
        (await ListIdsAsync(doctor, c.PatientId)).Should().BeEmpty();
        foreach (var id in byOwner)
        {
            await ExpectReadsAsync(both, c.PatientId, id, visible: true);
            await ExpectReadsAsync(filesOnly, c.PatientId, id, HttpStatusCode.Forbidden);
            await ExpectReadsAsync(doctor, c.PatientId, id, visible: false);
        }

        // The same holds for the doctor's own uploads in a "sensitive" category: visible because they uploaded it.
        var own = await (await UploadAsync(doctor, c.PatientId, Pdf, "id.pdf", "application/pdf", category: "Identity")).DataAsync(201);
        await ExpectReadsAsync(doctor, c.PatientId, own.GetProperty("id").GetGuid(), visible: true);
    }

    // ------------------------------------------------------------------ audit

    [Fact]
    public async Task Uploads_views_lists_and_downloads_are_audited_without_names_or_descriptions()
    {
        var c = await factory.BookableClinicAsync();
        var visitId = (await c.Owner.Client.RecordVisitAsync(c, null)).GetProperty("id").GetGuid();
        var linked = await (await UploadAsync(c.Owner.Client, c.PatientId, Pdf, "سري جداً.pdf", "application/pdf",
            category: "LabResult", description: "HIV-confidential", extra: new() { ["visitId"] = visitId.ToString() })).DataAsync(201);
        var linkedId = linked.GetProperty("id").GetGuid();
        var unlinkedId = await UploadedIdAsync(c.Owner.Client, c.PatientId);

        await (await c.Owner.Client.GetAsync($"/api/v1/attachments/{linkedId}")).DataAsync(200);
        await (await c.Owner.Client.GetAsync($"/api/v1/patients/{c.PatientId}/attachments?visitId={visitId}")).ReadJsonAsync();
        (await c.Owner.Client.GetAsync($"/api/v1/attachments/{linkedId}/content")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await factory.WithDbAsync(null, async db =>
        {
            var logs = await db.AuditLogs.Where(l => l.TenantId == c.Owner.TenantId && l.Action.StartsWith("file.")).ToListAsync();
            logs.Should().OnlyContain(l => l.UserId == c.Owner.UserId);
            logs.Should().OnlyContain(l => !(l.Details ?? "").Contains("سري") && !(l.Details ?? "").Contains("HIV") && !(l.Details ?? "").Contains(".pdf"));

            var uploadLinked = logs.Single(l => l.Action == AuditActions.FileUploaded && l.EntityId == linkedId.ToString());
            using (var d = JsonDocument.Parse(uploadLinked.Details!))
            {
                d.RootElement.GetProperty("visitId").GetGuid().Should().Be(visitId);
                d.RootElement.GetProperty("patientId").GetGuid().Should().Be(c.PatientId);
                d.RootElement.GetProperty("category").GetString().Should().Be("LabResult");
            }
            var uploadUnlinked = logs.Single(l => l.Action == AuditActions.FileUploaded && l.EntityId == unlinkedId.ToString());
            using (var d = JsonDocument.Parse(uploadUnlinked.Details!)) d.RootElement.TryGetProperty("visitId", out _).Should().BeFalse();

            var viewed = logs.Single(l => l.Action == AuditActions.FileViewed);
            viewed.EntityId.Should().Be(linkedId.ToString());
            using (var d = JsonDocument.Parse(viewed.Details!)) d.RootElement.GetProperty("visitId").GetGuid().Should().Be(visitId);

            var listed = logs.Single(l => l.Action == AuditActions.FilesListed);
            listed.EntityType.Should().Be("Patient");
            listed.EntityId.Should().Be(c.PatientId.ToString());
            using (var d = JsonDocument.Parse(listed.Details!))
            {
                d.RootElement.GetProperty("visitId").GetGuid().Should().Be(visitId);
                d.RootElement.GetProperty("returned").GetInt32().Should().Be(1);
            }
            // A quarantined download is refused, so no download is recorded.
            logs.Should().NotContain(l => l.Action == AuditActions.FileDownloaded);
        });
    }

    // ------------------------------------------------------------------ environment guard

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("QA")]
    public void Local_storage_is_refused_outside_development_and_testing(string environment)
    {
        // Valid production settings, so the configuration guard stays quiet and this boundary is what stops the start.
        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            ProductionSettings.Apply(b);
        });
        var start = () => app.CreateClient();
        start.Should().Throw<Exception>().Where(e => e.ToString().Contains("Local file storage is only allowed in Development and Testing"));
    }

    [Fact]
    public async Task Local_storage_is_allowed_in_development()
    {
        await using var app = factory.WithWebHostBuilder(b => b.UseEnvironment("Development"));
        (await app.CreateClient().GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ path safety

    [Theory]
    [InlineData("../../../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("..\\..\\Windows\\System32\\evil.pdf", "evil.pdf")]
    [InlineData("C:\\Users\\x\\report.pdf", "report.pdf")]
    [InlineData("/var/lib/secret.pdf", "secret.pdf")]
    [InlineData("invoice\u202Efdp.pdf", "invoicefdp.pdf")]
    [InlineData("a<b>c:d\"e|f?g*h.pdf", "a_b_c_d_e_f_g_h.pdf")]
    [InlineData("  spaced   name .pdf", "spaced name .pdf")]
    public async Task Malicious_file_names_are_sanitized_and_never_reach_the_filesystem(string supplied, string expected)
    {
        var c = await factory.BookableClinicAsync();
        var res = await UploadAsync(c.Owner.Client, c.PatientId, Pdf, supplied, "application/pdf");
        var a = await res.DataAsync(201);
        a.GetProperty("fileName").GetString().Should().Be(expected);

        var files = StoredFilesFor(c.Owner.TenantId);
        files.Should().ContainSingle().Which.Should().MatchRegex(StorageKeyFile.ToString());
        File.Exists("/etc/passwd.pdf").Should().BeFalse();
        Directory.EnumerateFiles(Path.GetDirectoryName(factory.StorageRoot)!, "*.pdf").Should().NotContain(f => f.EndsWith(expected));

        await MarkScanAsync(c.Owner.TenantId, a.GetProperty("id").GetGuid(), FileScanStatus.Scanning, FileScanStatus.Clean);
        var download = await c.Owner.Client.GetAsync($"/api/v1/attachments/{a.GetProperty("id").GetGuid()}/content");
        download.Content.Headers.ContentDisposition!.FileNameStar.Should().Be(expected);
    }

    [Fact]
    public async Task Local_storage_refuses_keys_that_are_not_server_generated()
    {
        var storage = factory.Services.GetRequiredService<IFileStorage>();
        var tenant = Guid.NewGuid().ToString("N");
        foreach (var key in new[] { "../outside", "/etc/passwd", "..\\x", "a/../../b", "", Guid.NewGuid().ToString("N"),
                     $"{tenant}/2026/10/../../../../etc", $"{tenant}/2026/10/{tenant}/x" })
        {
            await FluentActions.Awaiting(() => storage.OpenReadAsync(key)).Should().ThrowAsync<ArgumentException>();
            await FluentActions.Awaiting(() => storage.SaveAsync(key, new MemoryStream(Pdf))).Should().ThrowAsync<ArgumentException>();
            await FluentActions.Awaiting(() => storage.DeleteAsync(key)).Should().ThrowAsync<ArgumentException>();
        }
    }

    // ------------------------------------------------------------------ failures and cleanup

    [Fact]
    public async Task Storage_write_failure_is_503_without_metadata_or_internal_details()
    {
        var c = await factory.BookableClinicAsync();
        await using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IFileStorage>();
            s.AddSingleton<IFileStorage>(new FailingStorage(failWrite: true));
        }));
        var client = Authorized(failing, c.Owner.AccessToken);
        var res = await UploadAsync(client, c.PatientId, Pdf, "a.pdf", "application/pdf");
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await res.ErrorCodeAsync()).Should().Be("FILE_STORAGE_UNAVAILABLE");
        (await res.Content.ReadAsStringAsync()).Should().NotContainAny(FailingStorage.SecretPath, "IOException", "at ClinicSaaS");
        await ExpectNothingStoredAsync(c);
    }

    [Fact]
    public async Task Storage_read_failure_and_missing_bytes_are_503()
    {
        var c = await factory.BookableClinicAsync();
        var id = await UploadedIdAsync(c.Owner.Client, c.PatientId);
        await MarkScanAsync(c.Owner.TenantId, id, FileScanStatus.Scanning, FileScanStatus.Clean);

        await using (var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                     {
                         s.RemoveAll<IFileStorage>();
                         s.AddSingleton<IFileStorage>(new FailingStorage(failWrite: false));
                     })))
        {
            var res = await Authorized(failing, c.Owner.AccessToken).GetAsync($"/api/v1/attachments/{id}/content");
            res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await res.Content.ReadAsStringAsync()).Should().NotContain(FailingStorage.SecretPath);
        }

        foreach (var file in StoredFilesFor(c.Owner.TenantId)) File.Delete(Path.Combine(factory.StorageRoot, file));
        var missing = await c.Owner.Client.GetAsync($"/api/v1/attachments/{id}/content");
        missing.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await missing.ErrorCodeAsync()).Should().Be("FILE_STORAGE_UNAVAILABLE");
        (await missing.Content.ReadAsStringAsync()).Should().NotContain(factory.StorageRoot);
    }

    [Fact]
    public async Task Metadata_save_failure_removes_the_stored_bytes()
    {
        var c = await factory.BookableClinicAsync();
        await using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(new FailAttachmentInsert()))));
        var res = await UploadAsync(Authorized(failing, c.Owner.AccessToken), c.PatientId, Pdf, "a.pdf", "application/pdf");
        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await res.ErrorCodeAsync()).Should().Be("INTERNAL_ERROR");
        await ExpectNothingStoredAsync(c);
    }

    // ------------------------------------------------------------------ helpers

    private static ByteArrayContent FilePart(byte[] bytes, string fileName, string contentType)
    {
        var part = new ByteArrayContent(bytes);
        if (contentType.Length > 0) part.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        // Raw header so the supplied name reaches the server untouched (HttpClient would otherwise re-encode it).
        part.Headers.TryAddWithoutValidation("Content-Disposition",
            $"form-data; name=\"file\"; filename=\"{fileName.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}");
        return part;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid patientId, byte[] bytes, string fileName, string contentType,
        string? category = null, string? description = null, Dictionary<string, string>? extra = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(FilePart(bytes, fileName, contentType));
        if (category is not null) form.Add(new StringContent(category), "category");
        if (description is not null) form.Add(new StringContent(description), "description");
        foreach (var (k, v) in extra ?? []) form.Add(new StringContent(v), k);
        return await client.PostAsync($"/api/v1/patients/{patientId}/attachments", form);
    }

    private static async Task<Guid> UploadedIdAsync(HttpClient client, Guid patientId, Guid? visitId = null)
    {
        var res = await UploadAsync(client, patientId, Pdf, "doc.pdf", "application/pdf",
            extra: visitId is { } v ? new() { ["visitId"] = v.ToString() } : null);
        return (await res.DataAsync(201)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Expects the same outcome from list, metadata and download (one policy). <paramref name="visible"/> true: the
    /// file is listed, metadata 200 and the download reaches the quarantine gate (422, the file is still Pending —
    /// nothing marks it clean); false: not listed, 404 on both.
    /// </summary>
    private static async Task ExpectReadsAsync(HttpClient client, Guid patientId, Guid id, bool visible)
    {
        var listed = await ListIdsAsync(client, patientId);
        var meta = await client.GetAsync($"/api/v1/attachments/{id}");
        var content = await client.GetAsync($"/api/v1/attachments/{id}/content");
        if (visible)
        {
            listed.Should().Contain(id);
            meta.StatusCode.Should().Be(HttpStatusCode.OK);
            content.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await content.ErrorCodeAsync()).Should().Be("FILE_NOT_AVAILABLE");
        }
        else
        {
            listed.Should().NotContain(id);
            meta.StatusCode.Should().Be(HttpStatusCode.NotFound);
            content.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    /// <summary>The permission gate answers the same status on all three read endpoints.</summary>
    private static async Task ExpectReadsAsync(HttpClient client, Guid patientId, Guid id, HttpStatusCode status)
    {
        (await client.GetAsync($"/api/v1/patients/{patientId}/attachments")).StatusCode.Should().Be(status);
        (await client.GetAsync($"/api/v1/attachments/{id}")).StatusCode.Should().Be(status);
        (await client.GetAsync($"/api/v1/attachments/{id}/content")).StatusCode.Should().Be(status);
    }

    private static async Task<List<Guid>> ListIdsAsync(HttpClient client, Guid patientId)
    {
        var res = await client.GetAsync($"/api/v1/patients/{patientId}/attachments?pageSize=100");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.ReadJsonAsync()).GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
    }

    /// <summary>A doctor-template member (no MedicalRecords.ViewAllDoctors) with a doctor profile at the main branch.</summary>
    private async Task<(HttpClient Client, Guid DoctorId)> OwnRecordsDoctorAsync(Scenarios.BookableClinic c)
    {
        var (doctor, membershipId, _) = await factory.InviteAndAcceptAsync(c.Owner, "doctor");
        var doctorId = (await c.Owner.Client.CreateDoctorAsync(membershipId, Scenarios.Week(c.BranchId))).GetProperty("id").GetGuid();
        return (doctor, doctorId);
    }

    private static async Task<Guid> VisitAtAsync(HttpClient owner, QaScenarios.TwoBranchClinic t, Guid branchId)
    {
        var res = await owner.PostAsJsonAsync("/api/v1/visits", new { patientId = t.PatientId, doctorId = t.DoctorId, branchId });
        return (await res.DataAsync(201)).GetProperty("id").GetGuid();
    }

    /// <summary>Test fixture standing in for a future scanner: moves the state only through the domain's transitions.</summary>
    private Task MarkScanAsync(Guid tenantId, Guid id, params FileScanStatus[] path) =>
        factory.WithDbAsync(tenantId, async db =>
        {
            var a = await db.Set<FileAttachment>().SingleAsync(x => x.Id == id);
            foreach (var s in path) a.TransitionScanTo(s);
            await db.SaveChangesAsync();
        });

    private static async Task ExpectFieldErrorAsync(HttpResponseMessage res, string field)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        var body = await res.ReadJsonAsync();
        body.GetProperty("code").GetString().Should().Be("VALIDATION_FAILED");
        body.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue(body.GetRawText());
    }

    /// <summary>No metadata row and no stored bytes for this clinic.</summary>
    private async Task ExpectNothingStoredAsync(Scenarios.BookableClinic c)
    {
        StoredFilesFor(c.Owner.TenantId).Should().BeEmpty();
        await factory.WithDbAsync(c.Owner.TenantId, async db => (await db.Set<FileAttachment>().CountAsync()).Should().Be(0));
    }

    private string[] StoredFilesFor(Guid tenantId)
    {
        var dir = Path.Combine(factory.StorageRoot, tenantId.ToString("N"));
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(factory.StorageRoot, f).Replace('\\', '/')).ToArray()
            : [];
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> app, string token)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed class FailingStorage(bool failWrite) : IFileStorage
    {
        public const string SecretPath = "/srv/private/clinic-files";
        public Task SaveAsync(string key, Stream content, CancellationToken ct = default) =>
            failWrite ? throw new IOException($"Disk full at {SecretPath}/{key}") : Task.CompletedTask;
        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) => throw new IOException($"Read error at {SecretPath}/{key}");
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Makes the metadata insert fail after the bytes were stored (DB outage / constraint failure stand-in).</summary>
    private sealed class FailAttachmentInsert : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<FileAttachment>().Any(e => e.State == EntityState.Added))
                throw new DbUpdateException("Simulated metadata persistence failure");
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }
}
