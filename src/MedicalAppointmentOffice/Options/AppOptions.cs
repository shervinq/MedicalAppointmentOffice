namespace MedicalAppointmentOffice.Options;

public sealed class BaleOptions
{
    public const string SectionName = "Bale";

    public string Token { get; set; } = string.Empty;
    public string PaymentProviderToken { get; set; } = "WALLET-TEST-1111111111111111";
    public string PublicBaseUrl { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public long[] AdminUserIds { get; set; } = [];
    public bool RegisterWebhookOnStartup { get; set; } = true;
}

public sealed class BookingOptions
{
    public const string SectionName = "Booking";

    public string OfficeName { get; set; } = "سیستم نوبت‌گیری مطب دکتر قاسم‌زاده";
    public long PriceRials { get; set; } = 5_000_000;
    public int SlotMinutes { get; set; } = 15;
    public string EntryWindowStart { get; set; } = "14:00";
    public string EntryWindowEnd { get; set; } = "14:30";
    public string TimeZoneId { get; set; } = "Asia/Tehran";
    public int SearchDays { get; set; } = 60;
    public int ReservationMinutes { get; set; } = 15;
    public int SessionMinutes { get; set; } = 30;
    public int MinimumLeadMinutes { get; set; } = 60;
}
