using System.Text.Json.Serialization;

namespace MedicalAppointmentOffice.Bale;

public sealed class BaleUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; set; }

    [JsonPropertyName("message")]
    public BaleMessage? Message { get; set; }

    [JsonPropertyName("pre_checkout_query")]
    public BalePreCheckoutQuery? PreCheckoutQuery { get; set; }
}

public sealed class BaleMessage
{
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }

    [JsonPropertyName("from")]
    public BaleUser? From { get; set; }

    [JsonPropertyName("chat")]
    public BaleChat Chat { get; set; } = new();

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("contact")]
    public BaleContact? Contact { get; set; }

    [JsonPropertyName("successful_payment")]
    public BaleSuccessfulPayment? SuccessfulPayment { get; set; }
}

public sealed class BaleUser
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }
}

public sealed class BaleChat
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
}

public sealed class BaleContact
{
    [JsonPropertyName("phone_number")]
    public string PhoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("user_id")]
    public long? UserId { get; set; }
}

public sealed class BalePreCheckoutQuery
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public BaleUser From { get; set; } = new();

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = string.Empty;

    [JsonPropertyName("total_amount")]
    public long TotalAmount { get; set; }

    [JsonPropertyName("invoice_payload")]
    public string InvoicePayload { get; set; } = string.Empty;
}

public sealed class BaleSuccessfulPayment
{
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = string.Empty;

    [JsonPropertyName("total_amount")]
    public long TotalAmount { get; set; }

    [JsonPropertyName("invoice_payload")]
    public string InvoicePayload { get; set; } = string.Empty;

    [JsonPropertyName("telegram_payment_charge_id")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyName("provider_payment_charge_id")]
    public string ProviderTrackingCode { get; set; } = string.Empty;
}

public sealed record ReplyKeyboard(IReadOnlyList<IReadOnlyList<KeyboardButton>> Keyboard)
{
    [JsonPropertyName("resize_keyboard")]
    public bool ResizeKeyboard => true;
}

public sealed record KeyboardButton(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("request_contact")] bool RequestContact = false);

internal sealed class BaleApiResponse<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("error_code")]
    public int? ErrorCode { get; set; }
}
