using MedicalAppointmentOffice.Domain;
using Microsoft.EntityFrameworkCore;

namespace MedicalAppointmentOffice.Data;

public sealed class DatabaseInitializer(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        if (await db.WeeklySchedules.AnyAsync(cancellationToken))
        {
            return;
        }

        db.WeeklySchedules.AddRange(
            Schedule(DayOfWeek.Saturday, 17, 0, 1, 0),
            Schedule(DayOfWeek.Sunday, 17, 0, 1, 0),
            Schedule(DayOfWeek.Monday, 17, 0, 1, 0),
            Schedule(DayOfWeek.Tuesday, 17, 0, 1, 0),
            Schedule(DayOfWeek.Wednesday, 17, 0, 1, 0),
            Schedule(DayOfWeek.Thursday, 17, 0, 22, 0),
            new WeeklySchedule { DayOfWeek = DayOfWeek.Friday, IsEnabled = false });

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Default weekly office schedule was created.");
    }

    private static WeeklySchedule Schedule(
        DayOfWeek day,
        int startHour,
        int startMinute,
        int endHour,
        int endMinute) => new()
        {
            DayOfWeek = day,
            IsEnabled = true,
            StartMinute = (startHour * 60) + startMinute,
            EndMinute = (endHour * 60) + endMinute
        };
}
