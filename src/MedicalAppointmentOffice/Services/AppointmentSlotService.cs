using System.Data;
using MedicalAppointmentOffice.Data;
using MedicalAppointmentOffice.Domain;
using MedicalAppointmentOffice.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalAppointmentOffice.Services;

public sealed class ReservationGate
{
    public SemaphoreSlim Value { get; } = new(1, 1);
}

public sealed record ReservedSlot(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public sealed class AppointmentSlotService(
    IDbContextFactory<AppDbContext> contextFactory,
    ClinicSettingsService settingsService,
    IOptions<BookingOptions> options,
    IClock clock,
    TehranTime tehranTime,
    ReservationGate reservationGate,
    ILogger<AppointmentSlotService> logger)
{
    private readonly BookingOptions _options = options.Value;

    public async Task<ReservedSlot?> ReserveEarliestAsync(
        Guid appointmentId,
        CancellationToken cancellationToken = default)
    {
        await reservationGate.Value.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var now = clock.UtcNow;
            var runtimeSettings = await settingsService.GetAsync(cancellationToken);
            var slotMinutes = Math.Clamp(runtimeSettings.SlotMinutes, 5, 240);
            var appointment = await db.Appointments
                .Include(x => x.Reservation)
                .SingleAsync(x => x.Id == appointmentId, cancellationToken);

            if (appointment.Reservation is { } current &&
                (current.IsConfirmed || current.ExpiresAtUtc > now))
            {
                return new ReservedSlot(current.StartUtc, current.EndUtc);
            }

            if (appointment.Reservation is not null)
            {
                db.Reservations.Remove(appointment.Reservation);
                appointment.Reservation = null;
                await db.SaveChangesAsync(cancellationToken);
            }

            var schedules = await db.WeeklySchedules
                .AsNoTracking()
                .ToDictionaryAsync(x => x.DayOfWeek, cancellationToken);
            var exceptions = await db.ScheduleExceptions
                .AsNoTracking()
                .Where(x => x.LocalDate >= DateOnly.FromDateTime(tehranTime.ToLocal(now).DateTime))
                .ToDictionaryAsync(x => x.LocalDate, cancellationToken);
            var occupied = await db.Reservations
                .AsNoTracking()
                .Where(x => x.IsConfirmed || x.ExpiresAtUtc > now)
                .Select(x => new ReservedSlot(x.StartUtc, x.EndUtc))
                .ToListAsync(cancellationToken);

            foreach (var slot in EnumerateCandidates(now, schedules, exceptions, slotMinutes))
            {
                if (occupied.Any(x => Overlaps(slot, x)))
                {
                    continue;
                }

                var reservation = new AppointmentReservation
                {
                    AppointmentId = appointment.Id,
                    StartUtc = slot.StartUtc,
                    EndUtc = slot.EndUtc,
                    ExpiresAtUtc = now.AddMinutes(_options.ReservationMinutes)
                };
                db.Reservations.Add(reservation);

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return slot;
                }
                catch (DbUpdateException exception)
                {
                    logger.LogWarning(exception, "Slot {StartUtc} was claimed concurrently; trying the next slot.", slot.StartUtc);
                    db.Entry(reservation).State = EntityState.Detached;
                    appointment.Reservation = null;
                    occupied.Add(slot);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        finally
        {
            reservationGate.Value.Release();
        }
    }

    public async Task<int> ExpirePendingReservationsAsync(CancellationToken cancellationToken = default)
    {
        await reservationGate.Value.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var now = clock.UtcNow;
            var expired = await db.Reservations
                .Include(x => x.Appointment)
                .Where(x => !x.IsConfirmed && x.ExpiresAtUtc <= now)
                .ToListAsync(cancellationToken);

            foreach (var reservation in expired)
            {
                reservation.Appointment.Status = AppointmentStatus.Expired;
            }

            db.Reservations.RemoveRange(expired);
            await db.SaveChangesAsync(cancellationToken);
            return expired.Count;
        }
        finally
        {
            reservationGate.Value.Release();
        }
    }

    private IEnumerable<ReservedSlot> EnumerateCandidates(
        DateTimeOffset utcNow,
        IReadOnlyDictionary<DayOfWeek, WeeklySchedule> schedules,
        IReadOnlyDictionary<DateOnly, ScheduleException> exceptions,
        int slotMinutes)
    {
        var localNow = tehranTime.ToLocal(utcNow);
        var firstDate = DateOnly.FromDateTime(localNow.DateTime);
        var notBefore = utcNow.AddMinutes(_options.MinimumLeadMinutes);

        for (var offset = 0; offset < _options.SearchDays; offset++)
        {
            var date = firstDate.AddDays(offset);
            var day = date.DayOfWeek;
            if (!schedules.TryGetValue(day, out var schedule)) continue;

            var enabled = schedule.IsEnabled;
            var startMinute = schedule.StartMinute;
            var endMinute = schedule.EndMinute;

            if (exceptions.TryGetValue(date, out var exception))
            {
                if (exception.IsClosed) continue;
                enabled = exception.StartMinute.HasValue && exception.EndMinute.HasValue;
                startMinute = exception.StartMinute ?? startMinute;
                endMinute = exception.EndMinute ?? endMinute;
            }

            if (!enabled) continue;

            var adjustedEnd = endMinute <= startMinute ? endMinute + 1440 : endMinute;
            for (var minute = startMinute; minute + slotMinutes <= adjustedEnd; minute += slotMinutes)
            {
                var startDate = minute < 1440 ? date : date.AddDays(1);
                var start = tehranTime.ToUtc(startDate, minute % 1440);
                var end = start.AddMinutes(slotMinutes);
                if (start >= notBefore)
                {
                    yield return new ReservedSlot(start, end);
                }
            }
        }
    }

    private static bool Overlaps(ReservedSlot left, ReservedSlot right) =>
        left.StartUtc < right.EndUtc && right.StartUtc < left.EndUtc;
}
