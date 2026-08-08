using System.Text.Json.Serialization;
using MedicalAppointmentOffice.Bale;
using MedicalAppointmentOffice.Data;
using MedicalAppointmentOffice.Options;
using MedicalAppointmentOffice.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BaleOptions>(builder.Configuration.GetSection(BaleOptions.SectionName));
builder.Services.Configure<BookingOptions>(builder.Configuration.GetSection(BookingOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=data/appointments.db";
Directory.CreateDirectory("data");
builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(connectionString));

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BookingOptions>>().Value;
    return new TehranTime(options.TimeZoneId);
});
builder.Services.AddSingleton<BookingWindowService>();
builder.Services.AddSingleton<ReservationGate>();
builder.Services.AddScoped<AppointmentSlotService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<DatabaseInitializer>();

builder.Services.AddHttpClient<BaleClient>(client =>
{
    client.BaseAddress = new Uri("https://tapi.bale.ai/");
    client.Timeout = TimeSpan.FromSeconds(8);
});
builder.Services.AddHostedService<WebhookRegistrationService>();
builder.Services.AddHostedService<MaintenanceWorker>();

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync();
}

app.MapControllers();
app.MapGet("/health", HealthAsync);
app.MapGet("/", () => Results.Ok(new
{
    service = "سیستم نوبت‌گیری مطب دکتر قاسم‌زاده",
    status = "running"
}));

await app.RunAsync();

static async Task<IResult> HealthAsync(
    IDbContextFactory<AppDbContext> contextFactory,
    CancellationToken cancellationToken)
{
    await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
    if (await db.Database.CanConnectAsync(cancellationToken))
    {
        return Results.Ok(new { status = "healthy" });
    }

    return Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Database is unavailable");
}

public partial class Program { }
