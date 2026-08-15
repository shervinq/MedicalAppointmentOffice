using MedicalAppointmentOffice.Data;
using MedicalAppointmentOffice.Domain;
using Microsoft.EntityFrameworkCore;

namespace MedicalAppointmentOffice.Services;

public sealed class ClinicSettingsService(
    IDbContextFactory<AppDbContext> contextFactory,
    IClock clock)
{
    public async Task<ClinicSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ClinicSettings.AsNoTracking().SingleAsync(x => x.Id == 1, cancellationToken);
    }

    public async Task UpdateAsync(
        Action<ClinicSettings> update,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.ClinicSettings.SingleAsync(x => x.Id == 1, cancellationToken);
        update(settings);
        settings.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public static long InvoiceAmount(ClinicSettings settings) =>
        settings.PaymentMode == PaymentMode.Deposit
            ? Math.Clamp(settings.DepositRials, 1, settings.TotalPriceRials)
            : settings.TotalPriceRials;
}
