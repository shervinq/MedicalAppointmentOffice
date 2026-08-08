using System.Globalization;
using System.Text.RegularExpressions;

namespace MedicalAppointmentOffice.Services;

public static partial class InputValidators
{
    public static bool IsValidIranianNationalCode(string? value)
    {
        var code = NormalizeDigits(value).Replace("-", string.Empty, StringComparison.Ordinal);
        if (!NationalCodeRegex().IsMatch(code) || code.Distinct().Count() == 1)
        {
            return false;
        }

        var sum = 0;
        for (var index = 0; index < 9; index++)
        {
            sum += (code[index] - '0') * (10 - index);
        }

        var remainder = sum % 11;
        var checkDigit = code[9] - '0';
        return remainder < 2 ? checkDigit == remainder : checkDigit == 11 - remainder;
    }

    public static string NormalizeMobile(string? value)
    {
        var mobile = NormalizeDigits(value)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        if (mobile.StartsWith("+98", StringComparison.Ordinal))
        {
            mobile = $"0{mobile[3..]}";
        }
        else if (mobile.StartsWith("98", StringComparison.Ordinal) && mobile.Length == 12)
        {
            mobile = $"0{mobile[2..]}";
        }

        return MobileRegex().IsMatch(mobile) ? mobile : string.Empty;
    }

    public static bool TryParseClockRange(string? input, out int startMinute, out int endMinute)
    {
        startMinute = 0;
        endMinute = 0;
        var match = ClockRangeRegex().Match(NormalizeDigits(input));
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var startHour) ||
            !int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var startPart) ||
            !int.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var endHour) ||
            !int.TryParse(match.Groups[4].Value, CultureInfo.InvariantCulture, out var endPart) ||
            startHour > 23 || endHour > 23 || startPart > 59 || endPart > 59)
        {
            return false;
        }

        startMinute = (startHour * 60) + startPart;
        endMinute = (endHour * 60) + endPart;
        return startMinute != endMinute;
    }

    public static bool TryParsePersianOrGregorianDate(string? input, out DateOnly result)
    {
        result = default;
        var match = DateRegex().Match(NormalizeDigits(input));
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        try
        {
            if (year is >= 1300 and <= 1600)
            {
                var persian = new PersianCalendar();
                result = DateOnly.FromDateTime(persian.ToDateTime(year, month, day, 0, 0, 0, 0));
                return true;
            }

            return DateOnly.TryParseExact(
                $"{year:D4}-{month:D2}-{day:D2}",
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out result);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = source[index] switch
                {
                    >= '۰' and <= '۹' => (char)('0' + (source[index] - '۰')),
                    >= '٠' and <= '٩' => (char)('0' + (source[index] - '٠')),
                    _ => source[index]
                };
            }
        }).Trim();
    }

    [GeneratedRegex("^[0-9]{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex NationalCodeRegex();

    [GeneratedRegex("^09[0-9]{9}$", RegexOptions.CultureInvariant)]
    private static partial Regex MobileRegex();

    [GeneratedRegex("^\\s*([0-9]{1,2}):([0-9]{2})\\s*[-–—]\\s*([0-9]{1,2}):([0-9]{2})\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ClockRangeRegex();

    [GeneratedRegex("^\\s*([0-9]{4})[/-]([0-9]{1,2})[/-]([0-9]{1,2})\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();
}
