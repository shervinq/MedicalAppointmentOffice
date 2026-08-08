using Medical.Data.Sqlite;
using MedicalAppointmentOffice.Data;
using MedicalAppointmentOffice.Domain;
using MedicalAppointmentOffice.Options;
using MedicalAppointmentOffice.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MedicalAppointmentOffice.Tests;

public sealed class AppointmentSlotServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<AppDbContext> _dbOptions = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        await using var db = new AppDbContext(_dbOptions);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task FirstAvailableSlotIsReservedAndNeverDuplicated()
    {
        var appointment1 = await SeedAppointmentAsync(1001);
        var appointment2 = await SeedAppointmentAsync(1002);
        var service = CreateService(new DateTimeOffset(2026, 8, 8, 8, 30, 0, TimeSpan.Zero));

        var first = await service.ReserveEarliestAsync(appointment1);
        var second = await service.ReserveEarliestAsync(appointment2);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(TimeSpan.FromMinutes(15), first.EndUtc - first.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(15), second.EndUtc - second.StartUtc);
        Assert.NotEqual(first.StartUtc, second.StartUtc);
        Assert.Equal("17:00", TimeZoneInfo.ConvertTime(first.StartUtc, new TehranTime("Asia/Tehran").TimeZone).ToString("HH:mm"));
        Assert.Equal("17:15", TimeZoneInfo.ConvertTime(second.StartUtc, new TehranTime("Asia/Tehran").TimeZone).ToString("HH:mm"));
    }

    private AppointmentSlotService CreateService(DateTimeOffset now)
    {
        var options = Options.Create(new BookingOptions
        {
            SlotMinutes = 15,
            SearchDays = 7,
            MinimumLeadMinutes = 60,
            ReservationMinutes = 15
        });
        return new AppointmentSlotService(
            new TestDbContextFactory(_dbOptions),
            options,
            new FakeClock(now),
            new TehranTime("Asia/Tehran"),
            new ReservationGate(),
            NullLogger<AppointmentSlotService>.Instance);
    }

    private async Task<Guid> SeedAppointmentAsync(long userId)
    {
        await using var db = new AppDbContext(_dbOptions);
        if (!await db.WeeklySchedules.AnyAsync())
        {
            db.WeeklySchedules.AddRange(
                Enum.GetValues<DayOfWeek>().Select(day => new WeeklySchedule
                {
                    DayOfWeek = day,
                    IsEnabled = day == DayOfWeek.Saturday,
                    StartMinute = 17 * 60,
                    EndMinute = 18 * 60
                }));
        }

        var patient = new PatientProfile
        {
            BaleUserId = userId,
            ChatId = userId,
            FullName = "بیمار تست",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var appointment = new Appointment
        {
            PatientProfile = patient,
            InvoicePayload = $"appointment:{Guid.NewGuid():N}",
            AmountRials = 5_000_000,
            Status = AppointmentStatus.AwaitingPayment,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        return appointment.Id;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
