using MedicalAppointmentOffice.Options;
using Microsoft.Extensions.Options;

namespace MedicalAppointmentOffice.Services;

public sealed class BookingWindowService
{
    private readonly BookingOptions _options;
    private readonly TehranTime _tehranTime;
    private readonly TimeOnly _start;
    private readonly TimeOnly _end;

    public BookingWindowService(IOptions<BookingOptions> options, TehranTime tehranTime)
    {
        _options = options.Value;
        _tehranTime = tehranTime;
        _start = TimeOnly.ParseExact(_options.EntryWindowStart, "HH:mm");
        _end = TimeOnly.ParseExact(_options.EntryWindowEnd, "HH:mm");
    }

    public bool IsEntryOpen(DateTimeOffset utcNow)
    {
        var time = TimeOnly.FromDateTime(_tehranTime.ToLocal(utcNow).DateTime);
        return time >= _start && time < _end;
    }

    public DateTimeOffset GetNextOpening(DateTimeOffset utcNow)
    {
        var local = _tehranTime.ToLocal(utcNow);
        var date = DateOnly.FromDateTime(local.DateTime);
        var localTime = TimeOnly.FromDateTime(local.DateTime);

        if (localTime >= _start)
        {
            date = date.AddDays(1);
        }

        return _tehranTime.ToUtc(date, (_start.Hour * 60) + _start.Minute);
    }

    public string WindowLabel => $"{_options.EntryWindowStart} تا {_options.EntryWindowEnd}";
}
