using System.Text.RegularExpressions;

namespace ClinicSaaS.Domain.Common;

/// <summary>Saudi-specific format rules: mobile numbers, national ID / Iqama, VAT number.</summary>
public static partial class SaudiFormats
{
    /// <summary>
    /// Normalizes a Saudi mobile number to E.164 (+9665XXXXXXXX). Accepts 05XXXXXXXX, 5XXXXXXXX,
    /// 9665XXXXXXXX, +9665XXXXXXXX and 009665XXXXXXXX, with spaces/dashes and Arabic-Indic digits.
    /// Returns null when the input is not a valid Saudi mobile.
    /// </summary>
    public static string? NormalizeMobile(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(ToLatinDigits(input).Where(char.IsAsciiDigit).ToArray());

        if (digits.StartsWith("00966")) digits = digits[5..];
        else if (digits.StartsWith("966")) digits = digits[3..];
        else if (digits.StartsWith('0')) digits = digits[1..];

        return MobileLocalPart().IsMatch(digits) ? "+966" + digits : null;
    }

    public static bool IsValidMobile(string? input) => NormalizeMobile(input) is not null;

    /// <summary>
    /// Validates a Saudi national ID (starts with 1) or Iqama (starts with 2): 10 digits with the
    /// Luhn-style check digit used by the Ministry of Interior.
    /// </summary>
    public static bool IsValidNationalIdOrIqama(string? input)
    {
        if (input is null) return false;
        var id = ToLatinDigits(input).Trim();
        if (id.Length != 10 || !id.All(char.IsAsciiDigit) || (id[0] != '1' && id[0] != '2')) return false;

        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var d = id[i] - '0';
            if (i % 2 == 0)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }

    /// <summary>ZATCA VAT registration number: 15 digits, first and last digit are 3.</summary>
    public static bool IsValidVatNumber(string? input) =>
        input is not null && VatNumber().IsMatch(ToLatinDigits(input).Trim());

    /// <summary>Converts Arabic-Indic (٠-٩) and Eastern Arabic-Indic (۰-۹) digits to ASCII.</summary>
    public static string ToLatinDigits(string input)
    {
        var chars = input.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= '٠' and <= '٩') chars[i] = (char)('0' + (c - '٠'));
            else if (c is >= '۰' and <= '۹') chars[i] = (char)('0' + (c - '۰'));
        }
        return new string(chars);
    }

    [GeneratedRegex(@"^5\d{8}$")]
    private static partial Regex MobileLocalPart();

    [GeneratedRegex(@"^3\d{13}3$")]
    private static partial Regex VatNumber();
}
