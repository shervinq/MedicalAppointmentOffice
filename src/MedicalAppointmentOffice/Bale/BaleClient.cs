using System.Net.Http.Json;
using MedicalAppointmentOffice.Options;
using Microsoft.Extensions.Options;

namespace MedicalAppointmentOffice.Bale;

public sealed class BaleClient(
    HttpClient httpClient,
    IOptions<BaleOptions> options,
    ILogger<BaleClient> logger)
{
    private readonly BaleOptions _options = options.Value;

    public Task SendMessageAsync(
        long chatId,
        string text,
        ReplyKeyboard? keyboard = null,
        CancellationToken cancellationToken = default) =>
        CallAsync<object>("sendMessage", new
        {
            chat_id = chatId,
            text,
            reply_markup = keyboard
        }, cancellationToken);

    public Task SendInvoiceAsync(
        long chatId,
        string payload,
        long amountRials,
        CancellationToken cancellationToken = default) =>
        CallAsync<object>("sendInvoice", new
        {
            chat_id = chatId,
            title = "نوبت مطب دکتر قاسم‌زاده",
            description = "هزینه ثبت و قطعی‌سازی نوبت ویزیت",
            payload,
            provider_token = _options.PaymentProviderToken,
            prices = new[] { new { label = "ویزیت", amount = amountRials } }
        }, cancellationToken);

    public Task AnswerPreCheckoutAsync(
        string queryId,
        bool ok,
        string? errorMessage = null,
        CancellationToken cancellationToken = default) =>
        CallAsync<bool>("answerPreCheckoutQuery", new
        {
            pre_checkout_query_id = queryId,
            ok,
            error_message = errorMessage
        }, cancellationToken);

    public Task SetWebhookAsync(string url, CancellationToken cancellationToken = default) =>
        CallAsync<bool>("setWebhook", new { url }, cancellationToken);

    private async Task CallAsync<T>(string method, object payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("Bale:Token is not configured.");
        }

        using var response = await httpClient.PostAsJsonAsync(
            $"bot{_options.Token}/{method}",
            payload,
            cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<BaleApiResponse<T>>(
            cancellationToken: cancellationToken);

        if (!response.IsSuccessStatusCode || body is null || !body.Ok)
        {
            logger.LogError(
                "Bale API method {Method} failed with HTTP {StatusCode}, API error {ErrorCode}: {Description}",
                method,
                (int)response.StatusCode,
                body?.ErrorCode,
                body?.Description);
            throw new HttpRequestException($"Bale API method {method} failed: {body?.Description ?? response.ReasonPhrase}");
        }
    }
}
