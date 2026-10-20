using ClinicSaaS.Domain.Common;
using FluentValidation;

namespace ClinicSaaS.Application.Common;

/// <summary>Validation message codes. The API localizes them (ar/en); validators never hard-code UI text.</summary>
public static class ValidationCodes
{
    public const string Required = "required";
    public const string TooLong = "too_long";
    public const string TooShort = "too_short";
    public const string InvalidEmail = "invalid_email";
    public const string InvalidSaudiMobile = "invalid_saudi_mobile";
    public const string InvalidNationalId = "invalid_national_id";
    public const string InvalidVatNumber = "invalid_vat_number";
    public const string InvalidDate = "invalid_date";
    public const string OutOfRange = "out_of_range";
    public const string InvalidValue = "invalid_value";
    public const string WeakPassword = "weak_password";
    public const string InvalidSlug = "invalid_slug";
    public const string FileEmpty = "file_empty";
    public const string FileTooLarge = "file_too_large";
    public const string FileTypeNotAllowed = "file_type_not_allowed";
}

public static class ValidationExtensions
{
    public static IRuleBuilderOptions<T, string?> SaudiMobile<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(v => v is null || SaudiFormats.IsValidMobile(v)).WithMessage(ValidationCodes.InvalidSaudiMobile);

    public static IRuleBuilderOptions<T, string?> MaxLen<T>(this IRuleBuilder<T, string?> rule, int max) =>
        rule.MaximumLength(max).WithMessage(ValidationCodes.TooLong);

    public static IRuleBuilderOptions<T, string?> RequiredText<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage(ValidationCodes.Required);

    public static IRuleBuilderOptions<T, string?> ValidEmail<T>(this IRuleBuilder<T, string?> rule) =>
        rule.EmailAddress().WithMessage(ValidationCodes.InvalidEmail).MaximumLength(256).WithMessage(ValidationCodes.TooLong);

    /// <summary>Password policy: 10–128 chars, at least one letter and one digit (length over complexity, per NIST 800-63B).</summary>
    public static IRuleBuilderOptions<T, string?> StrongPassword<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(p => p is not null && p.Length is >= 10 and <= 128 && p.Any(char.IsLetter) && p.Any(char.IsDigit))
            .WithMessage(ValidationCodes.WeakPassword);

    /// <summary>Runs the validator and throws a <see cref="RequestValidationException"/> with field → codes.</summary>
    public static async Task ValidateOrThrowAsync<T>(this IValidator<T> validator, T instance, CancellationToken ct)
    {
        var result = await validator.ValidateAsync(instance, ct);
        if (result.IsValid) return;
        var errors = result.Errors
            .GroupBy(e => char.ToLowerInvariant(e.PropertyName[0]) + e.PropertyName[1..])
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
        throw new RequestValidationException(errors);
    }
}
