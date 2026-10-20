using ClinicSaaS.Api.Authorization;
using ClinicSaaS.Api.Errors;
using ClinicSaaS.Application.Attachments;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ClinicSaaS.Api.Controllers;

/// <summary>
/// Patient attachments (P1-02). Files.Upload gates upload; every read (list, metadata, download) needs Files.View AND
/// MedicalRecords.View — patient documents are clinical content, like a visit's clinical block or a prescription.
/// Tenant, branch and own-doctor scoping and the scan quarantine are enforced in <see cref="AttachmentService"/>.
/// Bytes are only ever served by
/// <c>GET /attachments/{id}/content</c>, always as a download, never through a public URL.
/// </summary>
[ApiController]
[Route("api/v1/attachments")]
public sealed class AttachmentsController(AttachmentService attachments) : ControllerBase
{
    /// <summary>File limit plus room for the multipart envelope and the small text fields.</summary>
    private const long MaxRequestBytes = AttachmentService.MaxFileBytes + 64 * 1024;

    /// <summary>multipart/form-data: <c>file</c> (required), <c>category</c>, <c>description</c>, <c>visitId</c>.</summary>
    [HttpPost("/api/v1/patients/{patientId:guid}/attachments")]
    [HasPermission(Permissions.Files.Upload)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes, ValueCountLimit = 16, KeyLengthLimit = 64)]
    [DisableFormValueModelBinding]
    public async Task<ActionResult<ApiResponse<AttachmentDto>>> Upload(Guid patientId, CancellationToken ct)
    {
        if (!Request.HasFormContentType)
            throw new RequestValidationException(new Dictionary<string, string[]> { ["file"] = [ValidationCodes.Required] });

        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(ct);
        }
        catch (InvalidDataException)
        {
            // Multipart body over MultipartBodyLengthLimit (or a malformed envelope).
            throw new AppException(ErrorCodes.PayloadTooLarge, 413);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw new AppException(ErrorCodes.PayloadTooLarge, 413);
        }

        if (form.Files.Count > 1)
            throw new RequestValidationException(new Dictionary<string, string[]> { ["file"] = [ValidationCodes.InvalidValue] });
        var file = form.Files.GetFile("file");
        Guid? visitId = null;
        if (form.TryGetValue("visitId", out var rawVisit) && !string.IsNullOrWhiteSpace(rawVisit))
        {
            if (!Guid.TryParse(rawVisit, out var parsed))
                throw new RequestValidationException(new Dictionary<string, string[]> { ["visitId"] = [ValidationCodes.InvalidValue] });
            visitId = parsed;
        }

        await using var content = file?.OpenReadStream();
        var created = await attachments.UploadAsync(patientId, new AttachmentUpload(
            file?.FileName, file?.ContentType, file?.Length ?? 0, content,
            form["category"].FirstOrDefault(), form["description"].FirstOrDefault(), visitId), ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, new ApiResponse<AttachmentDto>(created));
    }

    [HttpGet("/api/v1/patients/{patientId:guid}/attachments")]
    [HasPermission(Permissions.Files.View)]
    [HasPermission(Permissions.MedicalRecords.View)]
    public async Task<ActionResult<ApiPagedResponse<AttachmentDto>>> ListForPatient(
        Guid patientId, [FromQuery] AttachmentListQuery query, CancellationToken ct) =>
        Ok(ApiPagedResponse<AttachmentDto>.From(await attachments.ListForPatientAsync(patientId, query, ct)));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Files.View)]
    [HasPermission(Permissions.MedicalRecords.View)]
    public async Task<ActionResult<ApiResponse<AttachmentDto>>> Get(Guid id, CancellationToken ct) =>
        Ok(new ApiResponse<AttachmentDto>(await attachments.GetAsync(id, ct)));

    /// <summary>The bytes of a clean file (422 FILE_NOT_AVAILABLE while quarantined). Always Content-Disposition: attachment.</summary>
    [HttpGet("{id:guid}/content")]
    [HasPermission(Permissions.Files.View)]
    [HasPermission(Permissions.MedicalRecords.View)]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var download = await attachments.OpenDownloadAsync(id, ct);
        return File(download.Content, download.ContentType, download.FileName, enableRangeProcessing: false);
    }
}

/// <summary>
/// Keeps MVC model binding from reading the form before the action does, so the action alone reads it (once) and
/// can turn an oversized body into 413 instead of a generic model-binding error.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
