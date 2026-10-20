using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using FluentValidation;

namespace ClinicSaaS.Application.Patients;

public sealed record PatientListQuery : PageQuery
{
    public Gender? Gender { get; init; }
}

public sealed record PatientListItem(
    Guid Id, string MedicalRecordNumber, string FullName, string MobileNumber, Gender Gender, DateOnly? DateOfBirth, DateTime CreatedAtUtc);

/// <summary>
/// Patient details. The <see cref="Medical"/> block is null unless the caller holds MedicalRecords.View, and
/// the identity number is masked unless the caller can edit patients.
/// </summary>
public sealed record PatientDetails(
    Guid Id,
    string MedicalRecordNumber,
    string FullName,
    string? FullNameEn,
    string MobileNumber,
    string? Email,
    IdentityDocumentType IdentityType,
    string? IdentityNumber,
    DateOnly? DateOfBirth,
    Gender Gender,
    string? Nationality,
    string? Notes,
    PatientMedicalInfo? Medical,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    string RowVersion);

public sealed record PatientMedicalInfo(BloodGroup? BloodGroup, string? Allergies, string? ChronicConditions, string? MedicalNotes);

/// <summary>Demographics only — clinical fields go through <see cref="UpdatePatientMedicalRequest"/> (separate permission).</summary>
public record SavePatientRequest(
    string FullName,
    string? FullNameEn,
    string MobileNumber,
    string? Email,
    IdentityDocumentType IdentityType,
    string? IdentityNumber,
    DateOnly? DateOfBirth,
    Gender Gender,
    string? Nationality,
    string? Notes,
    Guid? RegisteredBranchId);

public sealed record UpdatePatientRequest(
    string FullName, string? FullNameEn, string MobileNumber, string? Email, IdentityDocumentType IdentityType,
    string? IdentityNumber, DateOnly? DateOfBirth, Gender Gender, string? Nationality, string? Notes, Guid? RegisteredBranchId,
    string RowVersion)
    : SavePatientRequest(FullName, FullNameEn, MobileNumber, Email, IdentityType, IdentityNumber, DateOfBirth, Gender, Nationality, Notes, RegisteredBranchId);

public sealed record UpdatePatientMedicalRequest(BloodGroup? BloodGroup, string? Allergies, string? ChronicConditions, string? MedicalNotes, string RowVersion);

public sealed record DuplicateCandidate(Guid Id, string MedicalRecordNumber, string FullName, string MobileNumber);

public sealed class SavePatientValidator : AbstractValidator<SavePatientRequest>
{
    public SavePatientValidator(Abstractions.IClock clock)
    {
        RuleFor(x => x.FullName).RequiredText().MinimumLength(2).WithMessage(ValidationCodes.TooShort).MaxLen(200);
        RuleFor(x => x.FullNameEn).MaxLen(200);
        RuleFor(x => x.MobileNumber).RequiredText().SaudiMobile();
        RuleFor(x => x.Email).ValidEmail().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Gender).IsInEnum().WithMessage(ValidationCodes.InvalidValue);
        RuleFor(x => x.IdentityType).IsInEnum().WithMessage(ValidationCodes.InvalidValue);
        RuleFor(x => x.IdentityNumber).RequiredText().When(x => x.IdentityType != IdentityDocumentType.None);
        RuleFor(x => x.IdentityNumber)
            .Must(SaudiFormats.IsValidNationalIdOrIqama).WithMessage(ValidationCodes.InvalidNationalId)
            .When(x => x.IdentityType is IdentityDocumentType.NationalId or IdentityDocumentType.Iqama);
        RuleFor(x => x.IdentityNumber).MaxLen(20);
        RuleFor(x => x.DateOfBirth)
            .Must(d => d is null || (d <= DateOnly.FromDateTime(clock.UtcNow) && d >= new DateOnly(1900, 1, 1)))
            .WithMessage(ValidationCodes.InvalidDate);
        RuleFor(x => x.Nationality).Matches("^[A-Z]{2}$").WithMessage(ValidationCodes.InvalidValue)
            .When(x => !string.IsNullOrEmpty(x.Nationality));
        RuleFor(x => x.Notes).MaxLen(1000);
    }
}

public sealed class UpdatePatientValidator : AbstractValidator<UpdatePatientRequest>
{
    public UpdatePatientValidator(Abstractions.IClock clock)
    {
        Include(new SavePatientValidator(clock));
        RuleFor(x => x.RowVersion).RequiredText();
    }
}

public sealed class UpdatePatientMedicalValidator : AbstractValidator<UpdatePatientMedicalRequest>
{
    public UpdatePatientMedicalValidator()
    {
        RuleFor(x => x.BloodGroup).IsInEnum().When(x => x.BloodGroup is not null).WithMessage(ValidationCodes.InvalidValue);
        RuleFor(x => x.Allergies).MaxLen(1000);
        RuleFor(x => x.ChronicConditions).MaxLen(1000);
        RuleFor(x => x.MedicalNotes).MaxLen(2000);
        RuleFor(x => x.RowVersion).RequiredText();
    }
}
