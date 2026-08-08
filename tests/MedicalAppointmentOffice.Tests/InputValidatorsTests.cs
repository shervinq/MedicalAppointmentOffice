using MedicalAppointmentOffice.Services;

namespace MedicalAppointmentOffice.Tests;

public sealed class InputValidatorsTests
{
    [Theory]
    [InlineData("1234567891", true)]
    [InlineData("۱۲۳۴۵۶۷۸۹۱", true)]
    [InlineData("1111111111", false)]
    [InlineData("1234567890", false)]
    [InlineData("123", false)]
    public void NationalCodeChecksumIsValidated(string value, bool expected)
    {
        Assert.Equal(expected, InputValidators.IsValidIranianNationalCode(value));
    }

    [Theory]
    [InlineData("09121234567", "09121234567")]
    [InlineData("+989121234567", "09121234567")]
    [InlineData("۰۹۱۲۱۲۳۴۵۶۷", "09121234567")]
    [InlineData("02112345678", "")]
    public void MobileNumbersAreNormalized(string value, string expected)
    {
        Assert.Equal(expected, InputValidators.NormalizeMobile(value));
    }

    [Fact]
    public void OvernightClockRangeIsAccepted()
    {
        var valid = InputValidators.TryParseClockRange("۱۷:۰۰ - ۰۱:۰۰", out var start, out var end);

        Assert.True(valid);
        Assert.Equal(17 * 60, start);
        Assert.Equal(60, end);
    }
}
