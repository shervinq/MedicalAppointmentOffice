using MedicalAppointmentOffice.Bale;
using MedicalAppointmentOffice.Data;
using MedicalAppointmentOffice.Domain;
using Microsoft.EntityFrameworkCore;

namespace MedicalAppointmentOffice.Services;

public sealed class MaintenanceWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Background appointment maintenance failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var slotService = scope.ServiceProvider.GetRequiredService<AppointmentSlotService>();
        var expiredCount = await slotService.ExpirePendingReservationsAsync(cancellationToken);
        if (expiredCount > 0)
        {
            logger.LogInformation("Released {Count} expired appointment reservations.", expiredCount);
        }

        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var baleClient = scope.ServiceProvider.GetRequiredService<BaleClient>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var tehranTime = scope.ServiceProvider.GetRequiredService<TehranTime>();
        var now = clock.UtcNow;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var upcoming = await db.Appointments
            .Include(x => x.PatientProfile)
            .Include(x => x.Reservation)
            .Where(x => x.Status == AppointmentStatus.Confirmed &&
                        x.Reservation != null &&
                        x.Reservation.StartUtc > now &&
                        ((!x.Reminder24HoursSent && x.Reservation.StartUtc <= now.AddHours(24)) ||
                         (!x.Reminder2HoursSent && x.Reservation.StartUtc <= now.AddHours(2))))
            .ToListAsync(cancellationToken);

        foreach (var appointment in upcoming)
        {
            var reservation = appointment.Reservation!;
            var isTwoHourReminder = reservation.StartUtc <= now.AddHours(2);
            var label = isTwoHourReminder ? "کمتر از دو ساعت" : "کمتر از ۲۴ ساعت";
            try
            {
                await baleClient.SendMessageAsync(
                    appointment.PatientProfile.ChatId,
                    $"⏰ یادآوری نوبت\n\n{label} تا نوبت شما باقی مانده است.\nزمان: {PersianFormatting.DateTime(reservation.StartUtc, tehranTime)}\nکد پیگیری: {appointment.TrackingCode}",
                    cancellationToken: cancellationToken);

                if (isTwoHourReminder)
                {
                    appointment.Reminder2HoursSent = true;
                    appointment.Reminder24HoursSent = true;
                }
                else
                {
                    appointment.Reminder24HoursSent = true;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not send reminder for appointment {AppointmentId}.", appointment.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
