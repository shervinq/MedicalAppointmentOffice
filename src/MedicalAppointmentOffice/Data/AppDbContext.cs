using MedicalAppointmentOffice.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MedicalAppointmentOffice.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<PatientProfile> Patients => Set<PatientProfile>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentReservation> Reservations => Set<AppointmentReservation>();
    public DbSet<WeeklySchedule> WeeklySchedules => Set<WeeklySchedule>();
    public DbSet<ScheduleException> ScheduleExceptions => Set<ScheduleException>();
    public DbSet<ClinicSettings> ClinicSettings => Set<ClinicSettings>();
    public DbSet<BotSession> BotSessions => Set<BotSession>();
    public DbSet<ProcessedUpdate> ProcessedUpdates => Set<ProcessedUpdate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PatientProfile>(entity =>
        {
            entity.HasIndex(x => x.BaleUserId).IsUnique();
            entity.Property(x => x.FullName).HasMaxLength(120);
            entity.Property(x => x.NationalCode).HasMaxLength(10);
            entity.Property(x => x.BirthDate).HasMaxLength(10);
            entity.Property(x => x.Mobile).HasMaxLength(15);
            entity.Property(x => x.InsuranceProvider).HasMaxLength(80);
            entity.Property(x => x.InsuranceNumber).HasMaxLength(80);
        });

        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.HasIndex(x => x.InvoicePayload).IsUnique();
            entity.HasIndex(x => x.TrackingCode).IsUnique();
            entity.HasIndex(x => x.BaleTransactionId).IsUnique();
            entity.Property(x => x.InvoicePayload).HasMaxLength(128);
            entity.Property(x => x.TrackingCode).HasMaxLength(16);
            entity.Property(x => x.BaleTransactionId).HasMaxLength(128);
            entity.Property(x => x.ProviderTrackingCode).HasMaxLength(128);
            entity.Property(x => x.Complaint).HasMaxLength(500);
            entity.Property(x => x.CancellationReason).HasMaxLength(300);
            entity.Ignore(x => x.EffectiveTotalPriceRials);
            entity.Ignore(x => x.RemainingRials);
            entity.HasOne(x => x.PatientProfile)
                .WithMany(x => x.Appointments)
                .HasForeignKey(x => x.PatientProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AppointmentReservation>(entity =>
        {
            entity.HasIndex(x => x.AppointmentId).IsUnique();
            entity.HasIndex(x => x.StartUtc).IsUnique();
            entity.HasOne(x => x.Appointment)
                .WithOne(x => x.Reservation)
                .HasForeignKey<AppointmentReservation>(x => x.AppointmentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WeeklySchedule>(entity => entity.HasIndex(x => x.DayOfWeek).IsUnique());
        modelBuilder.Entity<ScheduleException>(entity =>
        {
            entity.HasIndex(x => x.LocalDate).IsUnique();
            entity.Property(x => x.Note).HasMaxLength(200);
        });
        modelBuilder.Entity<ClinicSettings>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LastBookingReportLocalDate).HasMaxLength(10);
        });

        modelBuilder.Entity<BotSession>().HasKey(x => x.BaleUserId);
        modelBuilder.Entity<ProcessedUpdate>().HasKey(x => x.UpdateId);
        ConfigureDateTimeOffsetsForSqlite(modelBuilder);
    }

    private static void ConfigureDateTimeOffsetsForSqlite(ModelBuilder modelBuilder)
    {
        var converter = new ValueConverter<DateTimeOffset, long>(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        var nullableConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(converter);
                else if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(nullableConverter);
            }
        }
    }
}
