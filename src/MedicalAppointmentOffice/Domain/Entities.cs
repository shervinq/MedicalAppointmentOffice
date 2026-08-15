namespace MedicalAppointmentOffice.Domain;

public enum AppointmentStatus
{
    Draft = 0,
    AwaitingPayment = 1,
    Confirmed = 2,
    PaymentNeedsReview = 3,
    Cancelled = 4,
    Expired = 5
}

public enum PaymentMode
{
    Full = 0,
    Deposit = 1
}

public enum ConversationState
{
    Idle = 0,
    AwaitingFullName = 1,
    AwaitingNationalCode = 2,
    AwaitingBirthDate = 3,
    AwaitingMobile = 4,
    AwaitingInsuranceProvider = 5,
    AwaitingInsuranceNumber = 6,
    AwaitingComplaint = 7,
    AwaitingConfirmation = 8,
    AwaitingScheduleDay = 20,
    AwaitingScheduleRange = 21,
    AwaitingClosedDate = 22,
    AwaitingSlotMinutes = 30,
    AwaitingTotalPrice = 31,
    AwaitingPaymentMode = 32,
    AwaitingDepositAmount = 33
}

public sealed class PatientProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long BaleUserId { get; set; }
    public long ChatId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string NationalCode { get; set; } = string.Empty;
    public string BirthDate { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string InsuranceProvider { get; set; } = string.Empty;
    public string InsuranceNumber { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public ICollection<Appointment> Appointments { get; set; } = [];
}

public sealed class Appointment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PatientProfileId { get; set; }
    public PatientProfile PatientProfile { get; set; } = null!;
    public string Complaint { get; set; } = string.Empty;
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Draft;
    public long AmountRials { get; set; }
    public long TotalPriceRials { get; set; }
    public bool IsDepositPayment { get; set; }
    public string InvoicePayload { get; set; } = string.Empty;
    public string? TrackingCode { get; set; }
    public string? BaleTransactionId { get; set; }
    public string? ProviderTrackingCode { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? InvoiceSentAtUtc { get; set; }
    public DateTimeOffset? PaidAtUtc { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }
    public bool Reminder24HoursSent { get; set; }
    public bool Reminder2HoursSent { get; set; }
    public AppointmentReservation? Reservation { get; set; }

    public long EffectiveTotalPriceRials => TotalPriceRials > 0 ? TotalPriceRials : AmountRials;
    public long RemainingRials => Math.Max(0, EffectiveTotalPriceRials - AmountRials);
}

public sealed class AppointmentReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AppointmentId { get; set; }
    public Appointment Appointment { get; set; } = null!;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool IsConfirmed { get; set; }
}

public sealed class WeeklySchedule
{
    public int Id { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public bool IsEnabled { get; set; }
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
}

public sealed class ScheduleException
{
    public int Id { get; set; }
    public DateOnly LocalDate { get; set; }
    public bool IsClosed { get; set; } = true;
    public int? StartMinute { get; set; }
    public int? EndMinute { get; set; }
    public string? Note { get; set; }
}

public sealed class ClinicSettings
{
    public int Id { get; set; } = 1;
    public int SlotMinutes { get; set; } = 15;
    public long TotalPriceRials { get; set; } = 5_000_000;
    public PaymentMode PaymentMode { get; set; } = PaymentMode.Full;
    public long DepositRials { get; set; } = 1_000_000;
    public string? LastBookingReportLocalDate { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class BotSession
{
    public long BaleUserId { get; set; }
    public long ChatId { get; set; }
    public ConversationState State { get; set; }
    public Guid? DraftAppointmentId { get; set; }
    public string? Context { get; set; }
    public DateTimeOffset EntryGrantedUntilUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ProcessedUpdate
{
    public long UpdateId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
}
