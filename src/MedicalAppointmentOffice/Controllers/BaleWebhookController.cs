using System.Security.Cryptography;
using System.Text;
using MedicalAppointmentOffice.Bale;
using MedicalAppointmentOffice.Data;
using MedicalAppointmentOffice.Domain;
using MedicalAppointmentOffice.Options;
using MedicalAppointmentOffice.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalAppointmentOffice.Controllers;

[ApiController]
[Route("webhooks/bale/{secret}")]
public sealed class BaleWebhookController(
    IOptions<BaleOptions> options,
    IDbContextFactory<AppDbContext> contextFactory,
    ConversationService conversationService,
    IClock clock,
    ILogger<BaleWebhookController> logger) : ControllerBase
{
    private readonly BaleOptions _options = options.Value;

    [HttpPost]
    public async Task<IActionResult> ReceiveAsync(
        string secret,
        [FromBody] BaleUpdate update,
        CancellationToken cancellationToken)
    {
        if (!SecretsMatch(secret, _options.WebhookSecret))
        {
            return NotFound();
        }

        await using (var checkDb = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await checkDb.ProcessedUpdates.AnyAsync(x => x.UpdateId == update.UpdateId, cancellationToken))
            {
                return Ok();
            }
        }

        await conversationService.HandleAsync(update, cancellationToken);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.ProcessedUpdates.Add(new ProcessedUpdate
        {
            UpdateId = update.UpdateId,
            ProcessedAtUtc = clock.UtcNow
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogDebug(exception, "Bale update {UpdateId} was already recorded concurrently.", update.UpdateId);
        }

        return Ok();
    }

    private static bool SecretsMatch(string provided, string expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
