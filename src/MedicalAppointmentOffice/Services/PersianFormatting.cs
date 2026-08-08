using System.Globalization;

namespace MedicalAppointmentOffice.Services;

public static class PersianFormatting
{
    public static string DateTime(DateTimeOffset utc, TehranTime tehranTime)
    {
        var persianCalendar = new PersianCalendar();
        var local = tehranTime.ToLocal(utc);
        var date = local.DateTime;
        return $"{DayName(date.DayOfWeek)} {persianCalendar.GetYear(date):0000}/{persianCalendar.GetMonth(date):00}/{persianCalendar.GetDayOfMonth(date):00} ساعت {date:HH:mm}";
    }

    public static string Date(DateOnly date)
    {
        var persianCalendar = new PersianCalendar();
        var value = date.ToDateTime(TimeOnly.MinValue);
        return $"{persianCalendar.GetYear(value):0000}/{persianCalendar.GetMonth(value):00}/{persianCalendar.GetDayOfMonth(value):00}";
    }

    public static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Saturday => "شنبه",
        DayOfWeek.Sunday => "یکشنبه",
        DayOfWeek.Monday => "دوشنبه",
        DayOfWeek.Tuesday => "سه‌شنبه",
        DayOfWeek.Wednesday => "چهارشنبه",
        DayOfWeek.Thursday => "پنج‌شنبه",
        DayOfWeek.Friday => "جمعه",
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, null)
    };

    public static string Money(long amountRials) =>
        $"{amountRials / 10:N0} تومان";

    public static string Clock(int minute)
    {
        var normalized = ((minute % 1440) + 1440) % 1440;
        return $"{normalized / 60:00}:{normalized % 60:00}";
    }
}
