using MedicalAppointmentOffice.Options;
using MedicalAppointmentOffice.Services;
using Microsoft.Extensions.Options;

namespace MedicalAppointmentOffice.Tests;

public sealed class BookingWindowServiceTests
{
    private readonly BookingWindowService _service = new(
        Microsoft.Extensions.Options.Options.Create(new BookingOptions
        {
            EntryWindowStart = "14:00",
            EntryWindowEnd = "14:30"
        }),
        new TehranTime("Asia/Tehran"));

    [Theory]
    [InlineData("2026-08-08T10:29:59+00:00", false)]
    [InlineData("2026-08-08T10:30:00+00:00", true)]
    [InlineData("2026-08-08T10:59:59+00:00", true)]
    [InlineData("2026-08-08T11:00:00+00:00", false)]
    public void EntryWindowUsesTehranTime(string utcValue, bool expected)
    {
        Assert.Equal(expected, _service.IsEntryOpen(DateTimeOffset.Parse(utcValue)));
    }

    [Fact]
    public void NextOpeningMovesToTomorrowAfterWindow()
    {
        var next = _service.GetNextOpening(DateTimeOffset.Parse("2026-08-08T11:01:00+00:00"));

        Assert.Equal(DateTimeOffset.Parse("2026-08-09T10:30:00+00:00"), next);
    }
}
