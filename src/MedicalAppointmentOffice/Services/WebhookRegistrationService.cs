using MedicalAppointmentOffice.Bale;
using MedicalAppointmentOffice.Options;
using Microsoft.Extensions.Options;

namespace MedicalAppointmentOffice.Services;

public sealed class WebhookRegistrationService(
    IServiceProvider serviceProvider,
    IOptions<BaleOptions> options,
    IHostEnvironment environment,
    ILogger<WebhookRegistrationService> logger) : IHostedService
{
    private readonly BaleOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateProductionConfiguration();

        if (!_options.RegisterWebhookOnStartup ||
            string.IsNullOrWhiteSpace(_options.Token) ||
            string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
        {
            logger.LogInformation("Automatic Bale webhook registration is disabled or not configured.");
            return;
        }

        await using var scope = serviceProvider.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<BaleClient>();
        var url = $"{_options.PublicBaseUrl.TrimEnd('/')}/webhooks/bale/{_options.WebhookSecret}";
        await client.SetWebhookAsync(url, cancellationToken);
        logger.LogInformation("Bale webhook was registered for the configured public host.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void ValidateProductionConfiguration()
    {
        if (!environment.IsProduction())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("Bale:Token is required in Production.");
        }

        if (string.IsNullOrWhiteSpace(_options.PublicBaseUrl) ||
            !Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Bale:PublicBaseUrl must be a valid HTTPS URL in Production.");
        }

        if (_options.WebhookSecret.Length < 32)
        {
            throw new InvalidOperationException("Bale:WebhookSecret must contain at least 32 characters in Production.");
        }

        if (_options.AdminUserIds.Length == 0)
        {
            logger.LogWarning("No Bale admin is configured. Send /id to the bot and then set Bale:AdminUserIds.");
        }
    }
}
