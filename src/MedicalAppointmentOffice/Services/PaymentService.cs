using MedicalAppointmentOffice.Bale;
using MedicalAppointmentOffice.Data;
using MedicalAppointmentOffice.Domain;
using MedicalAppointmentOffice.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalAppointmentOffice.Services;

public sealed class PaymentService(
    IDbContextFactory<AppDbContext> contextFactory,
    AppointmentSlotService slotService,
    BaleClient baleClient,
    IOptions<BaleOptions> baleOptions,
    IClock clock,
    TehranTime tehranTime,
    ILogger<PaymentService> logger)
{
    private readonly BaleOptions _baleOptions = baleOptions.Value;

    public async Task HandlePreCheckoutAsync(
        BalePreCheckoutQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var appointment = await db.Appointments
                .Include(x => x.PatientProfile)
                .SingleOrDefaultAsync(x => x.InvoicePayload == query.InvoicePayload, cancellationToken);

            var validationError = ValidatePayment(
                appointment,
                query.From.Id,
                query.Currency,
                query.TotalAmount,
                allowExpired: false);

            if (validationError is not null)
            {
                await baleClient.AnswerPreCheckoutAsync(query.Id, false, validationError, cancellationToken);
                return;
            }

            var slot = await slotService.ReserveEarliestAsync(appointment!.Id, cancellationToken);
            if (slot is null)
            {
                await baleClient.AnswerPreCheckoutAsync(
                    query.Id,
                    false,
                    "در حال حاضر زمان خالی وجود ندارد و مبلغی از شما کسر نشد.",
                    cancellationToken);
                return;
            }

            await baleClient.AnswerPreCheckoutAsync(query.Id, true, cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Pre-checkout {QueryId} failed.", query.Id);
            try
            {
                await baleClient.AnswerPreCheckoutAsync(
                    query.Id,
                    false,
                    "خطای موقت در رزرو نوبت؛ لطفاً دوباره تلاش کنید.",
                    cancellationToken);
            }
            catch (Exception answerException)
            {
                logger.LogError(answerException, "Could not reject failed pre-checkout {QueryId}.", query.Id);
            }
        }
    }

    public async Task HandleSuccessfulPaymentAsync(
        BaleMessage message,
        CancellationToken cancellationToken = default)
    {
        var payment = message.SuccessfulPayment!;
        var userId = message.From?.Id ?? 0;

        await using var initialDb = await contextFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await initialDb.Appointments
            .Include(x => x.PatientProfile)
            .Include(x => x.Reservation)
            .SingleOrDefaultAsync(x => x.InvoicePayload == payment.InvoicePayload, cancellationToken);

        var validationError = ValidatePayment(
            appointment,
            userId,
            payment.Currency,
            payment.TotalAmount,
            allowExpired: true);
        if (validationError is not null)
        {
            logger.LogCritical(
                "Successful payment {TransactionId} failed validation: {Reason}",
                payment.TransactionId,
                validationError);
            await NotifyAdminsAsync(
                $"⚠️ پرداخت نیازمند بررسی دستی\nتراکنش: {payment.TransactionId}\nعلت: {validationError}",
                cancellationToken);
            return;
        }

        if (appointment!.Status == AppointmentStatus.Confirmed)
        {
            if (!string.Equals(appointment.BaleTransactionId, payment.TransactionId, StringComparison.Ordinal))
            {
                logger.LogCritical(
                    "A second transaction {TransactionId} was reported for confirmed appointment {AppointmentId}.",
                    payment.TransactionId,
                    appointment.Id);
                await NotifyAdminsAsync(
                    $"⚠️ پرداخت تکراری نیازمند بررسی\nنوبت: {appointment.TrackingCode}\nتراکنش جدید: {payment.TransactionId}",
                    cancellationToken);
            }

            return;
        }

        if (appointment.Reservation is null)
        {
            var recoveredSlot = await slotService.ReserveEarliestAsync(appointment.Id, cancellationToken);
            if (recoveredSlot is null)
            {
                await MarkPaymentForReviewAsync(appointment.Id, payment, cancellationToken);
                await baleClient.SendMessageAsync(
                    message.Chat.Id,
                    "پرداخت شما با موفقیت ثبت شد، اما تخصیص ساعت به بررسی ادمین نیاز دارد. مبلغ شما محفوظ است و نتیجه به‌زودی اعلام می‌شود.",
                    cancellationToken: cancellationToken);
                await NotifyAdminsAsync(
                    $"⚠️ پرداخت انجام شده اما ساعت خالی نیست\nتراکنش: {payment.TransactionId}\nکاربر: {userId}",
                    cancellationToken);
                return;
            }
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        appointment = await db.Appointments
            .Include(x => x.PatientProfile)
            .Include(x => x.Reservation)
            .SingleAsync(x => x.Id == appointment.Id, cancellationToken);

        if (appointment.Status == AppointmentStatus.Confirmed)
        {
            return;
        }

        appointment.Status = AppointmentStatus.Confirmed;
        appointment.PaidAtUtc = clock.UtcNow;
        appointment.BaleTransactionId = payment.TransactionId;
        appointment.ProviderTrackingCode = payment.ProviderTrackingCode;
        appointment.TrackingCode ??= CreateTrackingCode(appointment.Id);
        appointment.Reservation!.IsConfirmed = true;
        appointment.Reservation.ExpiresAtUtc = DateTimeOffset.MaxValue;
        await db.SaveChangesAsync(cancellationToken);

        await baleClient.SendMessageAsync(
            message.Chat.Id,
            BuildConfirmation(appointment),
            BaleKeyboards.Main(_baleOptions.AdminUserIds.Contains(userId)),
            cancellationToken);
    }

    private static string? ValidatePayment(
        Appointment? appointment,
        long userId,
        string currency,
        long totalAmount,
        bool allowExpired)
    {
        if (appointment is null)
        {
            return "صورتحساب معتبر نیست.";
        }

        if (appointment.PatientProfile.BaleUserId != userId)
        {
            return "این صورتحساب متعلق به حساب شما نیست.";
        }

        if (!currency.Equals("IRR", StringComparison.OrdinalIgnoreCase) || totalAmount != appointment.AmountRials)
        {
            return "مبلغ یا واحد پول صورتحساب صحیح نیست.";
        }

        if (appointment.Status == AppointmentStatus.Cancelled ||
            (!allowExpired && appointment.Status == AppointmentStatus.Expired))
        {
            return "این صورتحساب منقضی یا لغو شده است.";
        }

        if (!allowExpired && appointment.Status != AppointmentStatus.AwaitingPayment)
        {
            return "این صورتحساب دیگر قابل پرداخت نیست.";
        }

        if (allowExpired && appointment.Status == AppointmentStatus.Draft)
        {
            return "فرایند ثبت این صورتحساب کامل نشده است.";
        }

        return null;
    }

    private async Task MarkPaymentForReviewAsync(
        Guid appointmentId,
        BaleSuccessfulPayment payment,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await db.Appointments.SingleAsync(x => x.Id == appointmentId, cancellationToken);
        appointment.Status = AppointmentStatus.PaymentNeedsReview;
        appointment.PaidAtUtc = clock.UtcNow;
        appointment.BaleTransactionId = payment.TransactionId;
        appointment.ProviderTrackingCode = payment.ProviderTrackingCode;
        await db.SaveChangesAsync(cancellationToken);
    }

    private string BuildConfirmation(Appointment appointment)
    {
        var reservation = appointment.Reservation!;
        return $"""
            ✅ نوبت شما قطعی شد

            👤 بیمار: {appointment.PatientProfile.FullName}
            🗓 زمان: {PersianFormatting.DateTime(reservation.StartUtc, tehranTime)}
            ⏱ مدت ویزیت: ۱۵ دقیقه
            🔖 کد پیگیری: {appointment.TrackingCode}
            💳 کد تراکنش: {appointment.ProviderTrackingCode}

            لطفاً ۱۰ دقیقه زودتر در مطب حضور داشته باشید و کارت ملی و دفترچه/مدرک بیمه را همراه بیاورید.
            """;
    }

    private async Task NotifyAdminsAsync(string text, CancellationToken cancellationToken)
    {
        foreach (var adminId in _baleOptions.AdminUserIds)
        {
            try
            {
                await baleClient.SendMessageAsync(adminId, text, cancellationToken: cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not notify admin {AdminId}.", adminId);
            }
        }
    }

    private static string CreateTrackingCode(Guid id) =>
        $"GZ-{id:N}"[..11].ToUpperInvariant();
}
