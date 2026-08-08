namespace MedicalAppointmentOffice.Services;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class TehranTime
{
    public TehranTime(string configuredId)
    {
        TimeZone = Resolve(configuredId);
    }

    public TimeZoneInfo TimeZone { get; }

    public DateTimeOffset ToLocal(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, TimeZone);

    public DateTimeOffset ToUtc(DateOnly date, int minuteOfDay)
    {
        var dateTime = date.ToDateTime(TimeOnly.MinValue).AddMinutes(minuteOfDay);
        var unspecified = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static TimeZoneInfo Resolve(string configuredId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(configuredId);
        }
        catch (TimeZoneNotFoundException) when (!configuredId.Equals("Iran Standard Time", StringComparison.Ordinal))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");
        }
    }
}
