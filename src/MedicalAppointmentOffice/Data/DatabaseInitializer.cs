using MedicalAppointmentOffice.Domain;
using MedicalAppointmentOffice.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalAppointmentOffice.Data;

public sealed class DatabaseInitializer(
    IDbContextFactory<AppDbContext> contextFactory,
    IOptions<BookingOptions> bookingOptions,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureRuntimeSchemaAsync(db, cancellationToken);

        if (!await db.WeeklySchedules.AnyAsync(cancellationToken))
        {
            db.WeeklySchedules.AddRange(
                Schedule(DayOfWeek.Saturday, 17, 0, 1, 0),
                Schedule(DayOfWeek.Sunday, 17, 0, 1, 0),
                Schedule(DayOfWeek.Monday, 17, 0, 1, 0),
                Schedule(DayOfWeek.Tuesday, 17, 0, 1, 0),
                Schedule(DayOfWeek.Wednesday, 17, 0, 1, 0),
                Schedule(DayOfWeek.Thursday, 17, 0, 22, 0),
                new WeeklySchedule { DayOfWeek = DayOfWeek.Friday, IsEnabled = false });
        }

        if (!await db.ClinicSettings.AnyAsync(cancellationToken))
        {
            var defaults = bookingOptions.Value;
            db.ClinicSettings.Add(new ClinicSettings
            {
                Id = 1,
                SlotMinutes = defaults.SlotMinutes,
                TotalPriceRials = defaults.PriceRials,
                PaymentMode = PaymentMode.Full,
                DepositRials = Math.Min(1_000_000, defaults.PriceRials),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Database and configurable clinic settings are ready.");
    }

    private static async Task EnsureRuntimeSchemaAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ClinicSettings (
                Id INTEGER NOT NULL CONSTRAINT PK_ClinicSettings PRIMARY KEY,
                SlotMinutes INTEGER NOT NULL,
                TotalPriceRials INTEGER NOT NULL,
                PaymentMode INTEGER NOT NULL,
                DepositRials INTEGER NOT NULL,
                LastBookingReportLocalDate TEXT NULL,
                UpdatedAtUtc INTEGER NOT NULL
            );
            """, cancellationToken);

        await AddColumnIfMissingAsync(db, "Appointments", "TotalPriceRials", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "Appointments", "IsDepositPayment", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(
        AppDbContext db,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken);
        var exists = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }

        await reader.DisposeAsync();
        if (exists) return;

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
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
