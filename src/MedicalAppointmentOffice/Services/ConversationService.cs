using System.Globalization;
using System.Text;
using MedicalAppointmentOffice.Bale;
using MedicalAppointmentOffice.Data;
using MedicalAppointmentOffice.Domain;
using MedicalAppointmentOffice.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalAppointmentOffice.Services;

public sealed class ConversationService(
    IDbContextFactory<AppDbContext> contextFactory,
    BaleClient baleClient,
    PaymentService paymentService,
    BookingWindowService bookingWindow,
    ClinicSettingsService settingsService,
    IOptions<BookingOptions> bookingOptions,
    IOptions<BaleOptions> baleOptions,
    IClock clock,
    TehranTime tehranTime)
{
    private readonly BookingOptions _booking = bookingOptions.Value;
    private readonly BaleOptions _bale = baleOptions.Value;

    public async Task HandleAsync(BaleUpdate update, CancellationToken cancellationToken = default)
    {
        if (update.PreCheckoutQuery is not null)
        {
            await paymentService.HandlePreCheckoutAsync(update.PreCheckoutQuery, cancellationToken);
            return;
        }

        if (update.Message is not { } message || message.From is null) return;
        if (message.SuccessfulPayment is not null)
        {
            await paymentService.HandleSuccessfulPaymentAsync(message, cancellationToken);
            return;
        }

        var userId = message.From.Id;
        var chatId = message.Chat.Id;
        var text = (message.Text ?? string.Empty).Trim();
        var isAdmin = IsAdmin(userId);

        if (text.Equals("/id", StringComparison.OrdinalIgnoreCase))
        {
            await baleClient.SendMessageAsync(chatId, $"شناسه عددی بله شما: `{userId}`", cancellationToken: cancellationToken);
            return;
        }

        if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
        {
            await ResetSessionAsync(userId, chatId, cancellationToken);
            await SendWelcomeAsync(chatId, isAdmin, cancellationToken);
            return;
        }

        if (isAdmin && await TryHandleAdminCommandAsync(userId, chatId, text, cancellationToken)) return;

        var session = await GetOrCreateSessionAsync(userId, chatId, cancellationToken);
        if (text == "❌ انصراف")
        {
            await CancelDraftAndResetAsync(session, cancellationToken);
            await baleClient.SendMessageAsync(chatId, "فرایند فعلی لغو شد.", BaleKeyboards.MainMenu(isAdmin), cancellationToken);
            return;
        }

        if (isAdmin && session.State >= ConversationState.AwaitingScheduleDay)
        {
            await HandleAdminStateAsync(session, text, cancellationToken);
            return;
        }

        switch (session.State)
        {
            case ConversationState.AwaitingFullName: await SaveFullNameAsync(session, text, cancellationToken); break;
            case ConversationState.AwaitingNationalCode: await SaveNationalCodeAsync(session, text, cancellationToken); break;
            case ConversationState.AwaitingBirthDate: await SaveBirthDateAsync(session, text, cancellationToken); break;
            case ConversationState.AwaitingMobile: await SaveMobileAsync(session, message, cancellationToken); break;
            case ConversationState.AwaitingInsuranceProvider: await SaveInsuranceProviderAsync(session, text, cancellationToken); break;
            case ConversationState.AwaitingInsuranceNumber: await SaveInsuranceNumberAsync(session, text, cancellationToken); break;
            case ConversationState.AwaitingComplaint: await SaveComplaintAsync(session, text, cancellationToken); break;
            case ConversationState.AwaitingConfirmation: await HandleConfirmationAsync(session, text, cancellationToken); break;
            default: await HandleMainMenuAsync(userId, chatId, text, isAdmin, cancellationToken); break;
        }
    }

    private async Task HandleMainMenuAsync(long userId, long chatId, string text, bool isAdmin, CancellationToken cancellationToken)
    {
        switch (text)
        {
            case "🩺 دریافت نوبت": await StartBookingAsync(userId, chatId, cancellationToken); break;
            case "📌 پیگیری نوبت": await TrackAppointmentAsync(userId, chatId, cancellationToken); break;
            case "ℹ️ راهنما": await SendHelpAsync(chatId, isAdmin, cancellationToken); break;
            case "⚙️ مدیریت مطب" when isAdmin: await ShowAdminMenuAsync(userId, chatId, cancellationToken); break;
            default: await SendWelcomeAsync(chatId, isAdmin, cancellationToken); break;
        }
    }

    private async Task StartBookingAsync(long userId, long chatId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.BotSessions.SingleAsync(x => x.BaleUserId == userId, cancellationToken);

        var existing = await db.Appointments
            .Include(x => x.PatientProfile)
            .Include(x => x.Reservation)
            .Where(x => x.PatientProfile.BaleUserId == userId &&
                        (x.Status == AppointmentStatus.Confirmed || x.Status == AppointmentStatus.AwaitingPayment || x.Status == AppointmentStatus.PaymentNeedsReview))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing?.Status == AppointmentStatus.AwaitingPayment && existing.InvoiceSentAtUtc < now.AddMinutes(-_booking.SessionMinutes))
        {
            existing.Status = AppointmentStatus.Expired;
            if (existing.Reservation is not null) db.Reservations.Remove(existing.Reservation);
            await db.SaveChangesAsync(cancellationToken);
            existing = null;
        }

        if (existing?.Status == AppointmentStatus.Confirmed && existing.Reservation is { StartUtc: var start } && start > now)
        {
            await baleClient.SendMessageAsync(chatId, $"شما یک نوبت فعال دارید:\n{BuildAppointmentStatus(existing)}", BaleKeyboards.MainMenu(IsAdmin(userId)), cancellationToken);
            return;
        }
        if (existing?.Status == AppointmentStatus.PaymentNeedsReview)
        {
            await baleClient.SendMessageAsync(chatId, "پرداخت قبلی شما ثبت شده و در حال بررسی منشی است؛ تا اعلام نتیجه نوبت جدید ثبت نکنید.", cancellationToken: cancellationToken);
            return;
        }
        if (existing?.Status == AppointmentStatus.AwaitingPayment)
        {
            await baleClient.SendMessageAsync(chatId, "صورتحساب نوبت قبلی دوباره برای شما ارسال شد.", cancellationToken: cancellationToken);
            await baleClient.SendInvoiceAsync(chatId, existing.InvoicePayload, existing.AmountRials, cancellationToken);
            return;
        }
        if (!bookingWindow.IsEntryOpen(now))
        {
            var next = bookingWindow.GetNextOpening(now);
            await baleClient.SendMessageAsync(chatId, $"⏰ ثبت درخواست فقط هر روز از ساعت {bookingWindow.WindowLabel} باز است.\nنوبت بعدی بازشدن: {PersianFormatting.DateTime(next, tehranTime)}", BaleKeyboards.MainMenu(IsAdmin(userId)), cancellationToken);
            return;
        }

        var patient = await db.Patients.SingleOrDefaultAsync(x => x.BaleUserId == userId, cancellationToken);
        if (patient is null)
        {
            patient = new PatientProfile { BaleUserId = userId, ChatId = chatId, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Patients.Add(patient);
        }
        else patient.ChatId = chatId;

        var runtime = await settingsService.GetAsync(cancellationToken);
        var invoiceAmount = ClinicSettingsService.InvoiceAmount(runtime);
        var appointment = new Appointment
        {
            PatientProfile = patient,
            TotalPriceRials = runtime.TotalPriceRials,
            AmountRials = invoiceAmount,
            IsDepositPayment = runtime.PaymentMode == PaymentMode.Deposit,
            InvoicePayload = $"appointment:{Guid.NewGuid():N}",
            CreatedAtUtc = now
        };
        db.Appointments.Add(appointment);

        session.DraftAppointmentId = appointment.Id;
        session.EntryGrantedUntilUtc = now.AddMinutes(_booking.SessionMinutes);
        session.State = ProfileIsComplete(patient) ? ConversationState.AwaitingComplaint : ConversationState.AwaitingFullName;
        session.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);

        var prompt = ProfileIsComplete(patient)
            ? $"اطلاعات قبلی بیمار پیدا شد: {patient.FullName}\n\nلطفاً علت مراجعه یا توضیح کوتاه برای پزشک را بنویسید."
            : "برای ثبت نوبت، اطلاعات خود بیمار را وارد کنید.\n\nنام و نام خانوادگی بیمار چیست؟";
        await baleClient.SendMessageAsync(chatId, prompt, cancellationToken: cancellationToken);
    }

    private async Task SaveFullNameAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (text.Length is < 3 or > 120 || !text.Contains(' '))
        {
            await baleClient.SendMessageAsync(session.ChatId, "نام و نام خانوادگی کامل را وارد کنید (حداقل دو بخش).", cancellationToken: cancellationToken); return;
        }
        await UpdatePatientAndSessionAsync(session, p => p.FullName = text, ConversationState.AwaitingNationalCode, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, "کد ملی ۱۰ رقمی بیمار را وارد کنید.", cancellationToken: cancellationToken);
    }

    private async Task SaveNationalCodeAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        var nationalCode = InputValidators.NormalizeDigits(text).Replace("-", string.Empty, StringComparison.Ordinal);
        if (!InputValidators.IsValidIranianNationalCode(nationalCode))
        {
            await baleClient.SendMessageAsync(session.ChatId, "کد ملی معتبر نیست؛ لطفاً ۱۰ رقم را دوباره بررسی کنید.", cancellationToken: cancellationToken); return;
        }
        await UpdatePatientAndSessionAsync(session, p => p.NationalCode = nationalCode, ConversationState.AwaitingBirthDate, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, "تاریخ تولد بیمار را وارد کنید؛ مثال: ۱۳۷۰/۰۵/۲۱", cancellationToken: cancellationToken);
    }

    private async Task SaveBirthDateAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (!InputValidators.TryParsePersianOrGregorianDate(text, out var birthDate) || birthDate > DateOnly.FromDateTime(tehranTime.ToLocal(clock.UtcNow).DateTime) || birthDate.Year < 1900)
        {
            await baleClient.SendMessageAsync(session.ChatId, "فرمت تاریخ معتبر نیست؛ مانند ۱۳۷۰/۰۵/۲۱ وارد کنید.", cancellationToken: cancellationToken); return;
        }
        var normalized = InputValidators.NormalizeDigits(text).Replace('-', '/');
        await UpdatePatientAndSessionAsync(session, p => p.BirthDate = normalized, ConversationState.AwaitingMobile, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, "شماره موبایل بیمار را وارد کنید یا دکمه ارسال شماره را بزنید.", BaleKeyboards.Contact(), cancellationToken);
    }

    private async Task SaveMobileAsync(BotSession session, BaleMessage message, CancellationToken cancellationToken)
    {
        if (message.Contact?.UserId is { } contactUserId && contactUserId != session.BaleUserId)
        {
            await baleClient.SendMessageAsync(session.ChatId, "لطفاً شماره متعلق به حساب خودتان یا شماره بیمار را به‌صورت متنی ارسال کنید.", cancellationToken: cancellationToken); return;
        }
        var mobile = InputValidators.NormalizeMobile(message.Contact?.PhoneNumber ?? message.Text);
        if (string.IsNullOrEmpty(mobile))
        {
            await baleClient.SendMessageAsync(session.ChatId, "شماره معتبر باید ۱۱ رقم و با ۰۹ شروع شود.", BaleKeyboards.Contact(), cancellationToken); return;
        }
        await UpdatePatientAndSessionAsync(session, p => p.Mobile = mobile, ConversationState.AwaitingInsuranceProvider, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, "نام بیمه را وارد کنید؛ مانند تأمین اجتماعی. اگر بیمه ندارید بنویسید «بدون بیمه».", cancellationToken: cancellationToken);
    }

    private async Task SaveInsuranceProviderAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (text.Length is < 2 or > 80) { await baleClient.SendMessageAsync(session.ChatId, "نام بیمه معتبر نیست.", cancellationToken: cancellationToken); return; }
        await UpdatePatientAndSessionAsync(session, p => p.InsuranceProvider = text, ConversationState.AwaitingInsuranceNumber, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, "شماره بیمه/شماره دفترچه را وارد کنید؛ اگر ندارید بنویسید «ندارد».", cancellationToken: cancellationToken);
    }

    private async Task SaveInsuranceNumberAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (text.Length is < 2 or > 80) { await baleClient.SendMessageAsync(session.ChatId, "شماره بیمه معتبر نیست.", cancellationToken: cancellationToken); return; }
        await UpdatePatientAndSessionAsync(session, p => p.InsuranceNumber = InputValidators.NormalizeDigits(text), ConversationState.AwaitingComplaint, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, "علت مراجعه یا توضیح کوتاه برای پزشک را بنویسید.", cancellationToken: cancellationToken);
    }

    private async Task SaveComplaintAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (text.Length is < 3 or > 500) { await baleClient.SendMessageAsync(session.ChatId, "توضیح مراجعه باید بین ۳ تا ۵۰۰ کاراکتر باشد.", cancellationToken: cancellationToken); return; }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await GetDraftAsync(db, session, cancellationToken);
        appointment.Complaint = text;
        var tracked = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        tracked.State = ConversationState.AwaitingConfirmation;
        tracked.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, BuildReview(appointment), BaleKeyboards.Confirmation(), cancellationToken);
    }

    private async Task HandleConfirmationAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (text == "✏️ ویرایش اطلاعات")
        {
            await UpdateSessionStateAsync(session, ConversationState.AwaitingFullName, null, cancellationToken);
            await baleClient.SendMessageAsync(session.ChatId, "نام و نام خانوادگی بیمار را دوباره وارد کنید.", cancellationToken: cancellationToken); return;
        }
        if (text != "✅ تأیید و پرداخت")
        {
            await baleClient.SendMessageAsync(session.ChatId, "یکی از دکمه‌های تأیید، ویرایش یا انصراف را انتخاب کنید.", BaleKeyboards.Confirmation(), cancellationToken); return;
        }
        if (session.EntryGrantedUntilUtc < clock.UtcNow)
        {
            await CancelDraftAndResetAsync(session, cancellationToken);
            await baleClient.SendMessageAsync(session.ChatId, $"مهلت تکمیل فرم تمام شد. ثبت جدید در بازه {bookingWindow.WindowLabel} امکان‌پذیر است.", BaleKeyboards.MainMenu(IsAdmin(session.BaleUserId)), cancellationToken); return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await GetDraftAsync(db, session, cancellationToken);
        appointment.Status = AppointmentStatus.AwaitingPayment;
        appointment.InvoiceSentAtUtc = clock.UtcNow;
        var tracked = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        tracked.State = ConversationState.Idle;
        tracked.DraftAppointmentId = null;
        await db.SaveChangesAsync(cancellationToken);

        var label = appointment.IsDepositPayment ? "بیعانه" : "مبلغ کامل";
        await baleClient.SendMessageAsync(session.ChatId, $"صورتحساب {label} به مبلغ {PersianFormatting.Money(appointment.AmountRials)} ارسال می‌شود. ساعت دقیق از اولین زمان خالی و به‌ترتیب پرداخت قطعی خواهد شد.", cancellationToken: cancellationToken);
        await baleClient.SendInvoiceAsync(session.ChatId, appointment.InvoicePayload, appointment.AmountRials, cancellationToken);
    }

    private async Task TrackAppointmentAsync(long userId, long chatId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await db.Appointments.AsNoTracking().Include(x => x.PatientProfile).Include(x => x.Reservation)
            .Where(x => x.PatientProfile.BaleUserId == userId && x.Status != AppointmentStatus.Draft)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        await baleClient.SendMessageAsync(chatId, appointment is null ? "هنوز نوبتی برای شما ثبت نشده است." : BuildAppointmentStatus(appointment), BaleKeyboards.MainMenu(IsAdmin(userId)), cancellationToken);
    }

    private async Task<bool> TryHandleAdminCommandAsync(long userId, long chatId, string text, CancellationToken cancellationToken)
    {
        if (text is "/admin" or "⚙️ مدیریت مطب" or "🔙 منوی مدیریت") { await ShowAdminMenuAsync(userId, chatId, cancellationToken); return true; }
        if (text == "🔙 منوی اصلی") { await ResetSessionAsync(userId, chatId, cancellationToken); await SendWelcomeAsync(chatId, true, cancellationToken); return true; }
        if (text == "⚙️ تنظیمات نوبت‌دهی") { await ShowRuntimeSettingsAsync(userId, chatId, cancellationToken); return true; }
        if (text == "⏱ مدت هر بیمار") { await BeginSettingAsync(userId, chatId, ConversationState.AwaitingSlotMinutes, "مدت هر بیمار را به دقیقه وارد کنید (۵ تا ۲۴۰). مثال: 20", cancellationToken); return true; }
        if (text == "💵 مبلغ ویزیت") { await BeginSettingAsync(userId, chatId, ConversationState.AwaitingTotalPrice, "مبلغ کل ویزیت را به تومان وارد کنید. مثال: 500000", cancellationToken); return true; }
        if (text == "💳 نوع پرداخت") { await BeginSettingAsync(userId, chatId, ConversationState.AwaitingPaymentMode, "نوع پرداخت را انتخاب کنید.", cancellationToken, BaleKeyboards.PaymentMode()); return true; }
        if (text == "📌 مبلغ بیعانه") { await BeginSettingAsync(userId, chatId, ConversationState.AwaitingDepositAmount, "مبلغ بیعانه را به تومان وارد کنید. باید کمتر یا مساوی مبلغ کل باشد.", cancellationToken); return true; }
        if (text == "📅 ساعات هفتگی") { var session = await GetOrCreateSessionAsync(userId, chatId, cancellationToken); await SendScheduleAsync(session, cancellationToken); return true; }
        if (text == "🚫 تعطیلی تاریخ") { await BeginSettingAsync(userId, chatId, ConversationState.AwaitingClosedDate, "تاریخ تعطیلی را به‌شکل شمسی وارد کنید؛ مثال: ۱۴۰۵/۰۵/۲۱", cancellationToken); return true; }
        if (text == "📋 نوبت‌های امروز") { await ShowTodayAppointmentsAsync(chatId, cancellationToken); return true; }
        if (text == "🧾 گزارش کامل رزروها") { await ShowBookingReportAsync(chatId, DateOnly.FromDateTime(tehranTime.ToLocal(clock.UtcNow).DateTime), cancellationToken); return true; }
        if (text == "📊 آمار") { await ShowStatsAsync(chatId, cancellationToken); return true; }
        if (text.StartsWith("/cancel ", StringComparison.OrdinalIgnoreCase)) { await CancelAppointmentByAdminAsync(chatId, text[8..].Trim(), cancellationToken); return true; }
        if (text.StartsWith("/open ", StringComparison.OrdinalIgnoreCase)) { await OpenDateAsync(chatId, text[6..].Trim(), cancellationToken); return true; }
        return false;
    }

    private async Task ShowAdminMenuAsync(long userId, long chatId, CancellationToken cancellationToken)
    {
        var session = await GetOrCreateSessionAsync(userId, chatId, cancellationToken);
        await UpdateSessionStateAsync(session, ConversationState.Idle, null, cancellationToken);
        await baleClient.SendMessageAsync(chatId, "پنل مدیریت مطب\n\nتنظیمات نوبت‌دهی، پرداخت، برنامه کاری و گزارش رزروها از اینجا قابل مدیریت است.\nبرای لغو نوبت: /cancel GZ-XXXXXXXX علت لغو", BaleKeyboards.Admin(), cancellationToken);
    }

    private async Task ShowRuntimeSettingsAsync(long userId, long chatId, CancellationToken cancellationToken)
    {
        var session = await GetOrCreateSessionAsync(userId, chatId, cancellationToken);
        await UpdateSessionStateAsync(session, ConversationState.Idle, null, cancellationToken);
        var settings = await settingsService.GetAsync(cancellationToken);
        var mode = settings.PaymentMode == PaymentMode.Full ? "پرداخت کامل" : "بیعانه";
        await baleClient.SendMessageAsync(chatId,
            $"⚙️ تنظیمات فعلی\n\n⏱ مدت هر بیمار: {settings.SlotMinutes} دقیقه\n💵 مبلغ کل: {PersianFormatting.Money(settings.TotalPriceRials)}\n💳 نوع پرداخت: {mode}\n📌 مبلغ بیعانه: {PersianFormatting.Money(settings.DepositRials)}\n\nتغییرات فقط روی نوبت‌های جدید اعمال می‌شوند.",
            BaleKeyboards.Settings(), cancellationToken);
    }

    private async Task BeginSettingAsync(long userId, long chatId, ConversationState state, string prompt, CancellationToken cancellationToken, ReplyKeyboard? keyboard = null)
    {
        var session = await GetOrCreateSessionAsync(userId, chatId, cancellationToken);
        await UpdateSessionStateAsync(session, state, null, cancellationToken);
        await baleClient.SendMessageAsync(chatId, prompt, keyboard, cancellationToken);
    }

    private async Task HandleAdminStateAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        switch (session.State)
        {
            case ConversationState.AwaitingScheduleDay:
                if (!TryParseDay(text, out var day)) { await baleClient.SendMessageAsync(session.ChatId, "یکی از روزهای هفته را انتخاب کنید.", BaleKeyboards.WeekDays(), cancellationToken); return; }
                await UpdateSessionStateAsync(session, ConversationState.AwaitingScheduleRange, ((int)day).ToString(CultureInfo.InvariantCulture), cancellationToken);
                await baleClient.SendMessageAsync(session.ChatId, $"ساعت کاری {PersianFormatting.DayName(day)} را مانند 17:00-01:00 وارد کنید؛ یا بنویسید «تعطیل».", cancellationToken: cancellationToken);
                break;
            case ConversationState.AwaitingScheduleRange: await SaveScheduleRangeAsync(session, text, cancellationToken); break;
            case ConversationState.AwaitingClosedDate: await CloseDateAsync(session, text, cancellationToken); break;
            case ConversationState.AwaitingSlotMinutes: await SaveSlotMinutesAsync(session, text, cancellationToken); break;
            case ConversationState.AwaitingTotalPrice: await SaveTotalPriceAsync(session, text, cancellationToken); break;
            case ConversationState.AwaitingPaymentMode: await SavePaymentModeAsync(session, text, cancellationToken); break;
            case ConversationState.AwaitingDepositAmount: await SaveDepositAsync(session, text, cancellationToken); break;
        }
    }

    private async Task SaveSlotMinutesAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (!int.TryParse(InputValidators.NormalizeDigits(text), out var minutes) || minutes is < 5 or > 240)
        {
            await baleClient.SendMessageAsync(session.ChatId, "عدد معتبر بین ۵ تا ۲۴۰ دقیقه وارد کنید.", cancellationToken: cancellationToken); return;
        }
        await settingsService.UpdateAsync(x => x.SlotMinutes = minutes, cancellationToken);
        await ShowRuntimeSettingsAsync(session.BaleUserId, session.ChatId, cancellationToken);
    }

    private async Task SaveTotalPriceAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (!TryParseTomans(text, out var rials) || rials < 10_000)
        {
            await baleClient.SendMessageAsync(session.ChatId, "مبلغ معتبر را به تومان وارد کنید؛ مثال: 500000", cancellationToken: cancellationToken); return;
        }
        await settingsService.UpdateAsync(x => { x.TotalPriceRials = rials; if (x.DepositRials > rials) x.DepositRials = rials; }, cancellationToken);
        await ShowRuntimeSettingsAsync(session.BaleUserId, session.ChatId, cancellationToken);
    }

    private async Task SavePaymentModeAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        var mode = text switch { "💯 پرداخت کامل" => PaymentMode.Full, "📌 بیعانه" => PaymentMode.Deposit, _ => (PaymentMode)(-1) };
        if ((int)mode < 0) { await baleClient.SendMessageAsync(session.ChatId, "یکی از گزینه‌های پرداخت کامل یا بیعانه را انتخاب کنید.", BaleKeyboards.PaymentMode(), cancellationToken); return; }
        await settingsService.UpdateAsync(x => x.PaymentMode = mode, cancellationToken);
        await ShowRuntimeSettingsAsync(session.BaleUserId, session.ChatId, cancellationToken);
    }

    private async Task SaveDepositAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (!TryParseTomans(text, out var rials)) { await baleClient.SendMessageAsync(session.ChatId, "مبلغ معتبر را به تومان وارد کنید.", cancellationToken: cancellationToken); return; }
        var settings = await settingsService.GetAsync(cancellationToken);
        if (rials <= 0 || rials > settings.TotalPriceRials) { await baleClient.SendMessageAsync(session.ChatId, "بیعانه باید بیشتر از صفر و کمتر یا مساوی مبلغ کل ویزیت باشد.", cancellationToken: cancellationToken); return; }
        await settingsService.UpdateAsync(x => x.DepositRials = rials, cancellationToken);
        await ShowRuntimeSettingsAsync(session.BaleUserId, session.ChatId, cancellationToken);
    }

    private async Task SaveScheduleRangeAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (!int.TryParse(session.Context, CultureInfo.InvariantCulture, out var dayNumber) || !Enum.IsDefined(typeof(DayOfWeek), dayNumber)) { await ShowAdminMenuAsync(session.BaleUserId, session.ChatId, cancellationToken); return; }
        var day = (DayOfWeek)dayNumber;
        var isClosed = text is "تعطیل" or "بسته";
        var start = 0; var end = 0;
        if (!isClosed && !InputValidators.TryParseClockRange(text, out start, out end)) { await baleClient.SendMessageAsync(session.ChatId, "فرمت صحیح نیست؛ نمونه: 17:00-01:00 یا «تعطیل».", cancellationToken: cancellationToken); return; }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var schedule = await db.WeeklySchedules.SingleAsync(x => x.DayOfWeek == day, cancellationToken);
        schedule.IsEnabled = !isClosed;
        if (!isClosed) { schedule.StartMinute = start; schedule.EndMinute = end; }
        var tracked = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        tracked.State = ConversationState.AwaitingScheduleDay; tracked.Context = null;
        await db.SaveChangesAsync(cancellationToken);
        var result = isClosed ? "تعطیل" : $"{PersianFormatting.Clock(start)} تا {PersianFormatting.Clock(end)}";
        await baleClient.SendMessageAsync(session.ChatId, $"✅ برنامه {PersianFormatting.DayName(day)} روی «{result}» تنظیم شد. روز دیگری را انتخاب کنید.", BaleKeyboards.WeekDays(), cancellationToken);
    }

    private async Task CloseDateAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (!InputValidators.TryParsePersianOrGregorianDate(text, out var date)) { await baleClient.SendMessageAsync(session.ChatId, "تاریخ معتبر نیست؛ نمونه شمسی: ۱۴۰۵/۰۵/۲۱", cancellationToken: cancellationToken); return; }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var exception = await db.ScheduleExceptions.SingleOrDefaultAsync(x => x.LocalDate == date, cancellationToken) ?? new ScheduleException { LocalDate = date };
        if (exception.Id == 0) db.ScheduleExceptions.Add(exception);
        exception.IsClosed = true; exception.Note = "تعطیلی ثبت‌شده توسط ادمین";
        var tracked = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken); tracked.State = ConversationState.Idle;
        await db.SaveChangesAsync(cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, $"✅ تاریخ {PersianFormatting.Date(date)} تعطیل شد. برای بازکردن دوباره بنویسید: /open {PersianFormatting.Date(date)}", BaleKeyboards.Admin(), cancellationToken);
    }

    private async Task OpenDateAsync(long chatId, string input, CancellationToken cancellationToken)
    {
        if (!InputValidators.TryParsePersianOrGregorianDate(input, out var date)) { await baleClient.SendMessageAsync(chatId, "تاریخ معتبر نیست.", cancellationToken: cancellationToken); return; }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var exception = await db.ScheduleExceptions.SingleOrDefaultAsync(x => x.LocalDate == date, cancellationToken);
        if (exception is not null) { db.ScheduleExceptions.Remove(exception); await db.SaveChangesAsync(cancellationToken); }
        await baleClient.SendMessageAsync(chatId, $"تاریخ {PersianFormatting.Date(date)} مطابق برنامه هفتگی باز شد.", BaleKeyboards.Admin(), cancellationToken);
    }

    private async Task CancelAppointmentByAdminAsync(long chatId, string arguments, CancellationToken cancellationToken)
    {
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { await baleClient.SendMessageAsync(chatId, "نمونه: /cancel GZ-12345678 علت لغو", cancellationToken: cancellationToken); return; }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var code = parts[0].ToUpperInvariant();
        var appointment = await db.Appointments.Include(x => x.PatientProfile).Include(x => x.Reservation).SingleOrDefaultAsync(x => x.TrackingCode == code, cancellationToken);
        if (appointment is null) { await baleClient.SendMessageAsync(chatId, "کد پیگیری پیدا نشد.", cancellationToken: cancellationToken); return; }
        appointment.Status = AppointmentStatus.Cancelled; appointment.CancelledAtUtc = clock.UtcNow; appointment.CancellationReason = parts.Length == 2 ? parts[1] : "لغو توسط مطب";
        if (appointment.Reservation is not null) db.Reservations.Remove(appointment.Reservation);
        await db.SaveChangesAsync(cancellationToken);
        await baleClient.SendMessageAsync(appointment.PatientProfile.ChatId, $"نوبت {appointment.TrackingCode} توسط مطب لغو شد.\nعلت: {appointment.CancellationReason}\nبرای هماهنگی بازپرداخت با مطب تماس بگیرید.", cancellationToken: cancellationToken);
        await baleClient.SendMessageAsync(chatId, "نوبت لغو و زمان آن آزاد شد. بازپرداخت وجه باید طبق روال مطب انجام شود.", BaleKeyboards.Admin(), cancellationToken);
    }

    private async Task ShowTodayAppointmentsAsync(long chatId, CancellationToken cancellationToken)
    {
        var date = DateOnly.FromDateTime(tehranTime.ToLocal(clock.UtcNow).DateTime);
        var start = tehranTime.ToUtc(date, 0); var end = tehranTime.ToUtc(date.AddDays(1), 0);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var appointments = await db.Appointments.AsNoTracking().Include(x => x.PatientProfile).Include(x => x.Reservation)
            .Where(x => x.Status == AppointmentStatus.Confirmed && x.Reservation != null && x.Reservation.StartUtc >= start && x.Reservation.StartUtc < end)
            .OrderBy(x => x.Reservation!.StartUtc).ToListAsync(cancellationToken);
        if (appointments.Count == 0) { await baleClient.SendMessageAsync(chatId, "برای امروز نوبت قطعی ثبت نشده است.", BaleKeyboards.Admin(), cancellationToken); return; }
        var builder = new StringBuilder("📋 نوبت‌های امروز\n\n");
        foreach (var a in appointments) builder.Append(tehranTime.ToLocal(a.Reservation!.StartUtc).ToString("HH:mm", CultureInfo.InvariantCulture)).Append(" — ").Append(a.PatientProfile.FullName).Append(" — ").Append(a.PatientProfile.Mobile).Append(" — ").Append(a.TrackingCode).AppendLine();
        await SendLongMessageAsync(chatId, builder.ToString(), cancellationToken);
    }

    public async Task ShowBookingReportAsync(long chatId, DateOnly bookingDate, CancellationToken cancellationToken = default)
    {
        var start = tehranTime.ToUtc(bookingDate, 0); var end = tehranTime.ToUtc(bookingDate.AddDays(1), 0);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var appointments = await db.Appointments.AsNoTracking().Include(x => x.PatientProfile).Include(x => x.Reservation)
            .Where(x => x.Status == AppointmentStatus.Confirmed && x.CreatedAtUtc >= start && x.CreatedAtUtc < end)
            .OrderBy(x => x.Reservation!.StartUtc).ToListAsync(cancellationToken);
        if (appointments.Count == 0) { await baleClient.SendMessageAsync(chatId, $"🧾 در تاریخ {PersianFormatting.Date(bookingDate)} رزرو قطعی ثبت نشده است.", BaleKeyboards.Admin(), cancellationToken); return; }
        var builder = new StringBuilder($"🧾 گزارش کامل رزروهای {PersianFormatting.Date(bookingDate)}\nتعداد: {appointments.Count}\n\n");
        var index = 1;
        foreach (var a in appointments)
        {
            var duration = a.Reservation is null ? 0 : (int)(a.Reservation.EndUtc - a.Reservation.StartUtc).TotalMinutes;
            builder.Append(index++).Append(") ").AppendLine(a.PatientProfile.FullName)
                .Append("🪪 کد ملی: ").AppendLine(a.PatientProfile.NationalCode)
                .Append("🎂 تولد: ").AppendLine(a.PatientProfile.BirthDate)
                .Append("📱 موبایل: ").AppendLine(a.PatientProfile.Mobile)
                .Append("🛡 بیمه: ").Append(a.PatientProfile.InsuranceProvider).Append(" / ").AppendLine(a.PatientProfile.InsuranceNumber)
                .Append("📝 علت مراجعه: ").AppendLine(a.Complaint)
                .Append("🗓 زمان نوبت: ").AppendLine(a.Reservation is null ? "-" : PersianFormatting.DateTime(a.Reservation.StartUtc, tehranTime))
                .Append("⏱ مدت: ").Append(duration).AppendLine(" دقیقه")
                .Append("💳 پرداخت‌شده: ").AppendLine(PersianFormatting.Money(a.AmountRials))
                .Append("💰 مانده: ").AppendLine(PersianFormatting.Money(a.RemainingRials))
                .Append("🔖 کد پیگیری: ").AppendLine(a.TrackingCode).AppendLine("────────────");
        }
        await SendLongMessageAsync(chatId, builder.ToString(), cancellationToken);
    }

    private async Task ShowStatsAsync(long chatId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var confirmed = await db.Appointments.CountAsync(x => x.Status == AppointmentStatus.Confirmed, cancellationToken);
        var pending = await db.Appointments.CountAsync(x => x.Status == AppointmentStatus.AwaitingPayment, cancellationToken);
        var needsReview = await db.Appointments.CountAsync(x => x.Status == AppointmentStatus.PaymentNeedsReview, cancellationToken);
        var paid = await db.Appointments.Where(x => x.Status == AppointmentStatus.Confirmed).SumAsync(x => (long?)x.AmountRials, cancellationToken) ?? 0;
        var total = await db.Appointments.Where(x => x.Status == AppointmentStatus.Confirmed).SumAsync(x => (long?)(x.TotalPriceRials > 0 ? x.TotalPriceRials : x.AmountRials), cancellationToken) ?? 0;
        await baleClient.SendMessageAsync(chatId, $"📊 آمار کل\n\nنوبت قطعی: {confirmed:N0}\nدر انتظار پرداخت: {pending:N0}\nنیازمند بررسی: {needsReview:N0}\nدریافتی آنلاین: {PersianFormatting.Money(paid)}\nمانده قابل وصول: {PersianFormatting.Money(Math.Max(0, total - paid))}", BaleKeyboards.Admin(), cancellationToken);
    }

    private async Task SendScheduleAsync(BotSession session, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var schedules = await db.WeeklySchedules.AsNoTracking().OrderBy(x => ((int)x.DayOfWeek + 1) % 7).ToListAsync(cancellationToken);
        var builder = new StringBuilder("📅 ساعات هفتگی فعلی\n\n");
        foreach (var schedule in schedules) builder.Append(PersianFormatting.DayName(schedule.DayOfWeek)).Append(": ").AppendLine(schedule.IsEnabled ? $"{PersianFormatting.Clock(schedule.StartMinute)} تا {PersianFormatting.Clock(schedule.EndMinute)}" : "تعطیل");
        builder.Append("\nبرای تغییر، روز را انتخاب کنید.");
        await UpdateSessionStateAsync(session, ConversationState.AwaitingScheduleDay, null, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, builder.ToString(), BaleKeyboards.WeekDays(), cancellationToken);
    }

    private async Task UpdatePatientAndSessionAsync(BotSession session, Action<PatientProfile> update, ConversationState nextState, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var patient = await db.Patients.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        update(patient); patient.UpdatedAtUtc = clock.UtcNow;
        var tracked = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken); tracked.State = nextState; tracked.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<BotSession> GetOrCreateSessionAsync(long userId, long chatId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.BotSessions.SingleOrDefaultAsync(x => x.BaleUserId == userId, cancellationToken);
        if (session is null) { session = new BotSession { BaleUserId = userId, ChatId = chatId, State = ConversationState.Idle, UpdatedAtUtc = clock.UtcNow }; db.BotSessions.Add(session); }
        else session.ChatId = chatId;
        await db.SaveChangesAsync(cancellationToken); return session;
    }

    private async Task ResetSessionAsync(long userId, long chatId, CancellationToken cancellationToken) => await UpdateSessionStateAsync(await GetOrCreateSessionAsync(userId, chatId, cancellationToken), ConversationState.Idle, null, cancellationToken);

    private async Task UpdateSessionStateAsync(BotSession session, ConversationState state, string? context, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tracked = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        tracked.State = state; tracked.Context = context; tracked.UpdatedAtUtc = clock.UtcNow;
        if (state == ConversationState.Idle) tracked.DraftAppointmentId = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CancelDraftAndResetAsync(BotSession session, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tracked = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        if (tracked.DraftAppointmentId is { } id)
        {
            var appointment = await db.Appointments.Include(x => x.Reservation).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (appointment is not null && appointment.Status == AppointmentStatus.Draft) { appointment.Status = AppointmentStatus.Cancelled; appointment.CancelledAtUtc = clock.UtcNow; appointment.CancellationReason = "لغو توسط کاربر پیش از پرداخت"; }
        }
        tracked.State = ConversationState.Idle; tracked.DraftAppointmentId = null; tracked.Context = null; tracked.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Appointment> GetDraftAsync(AppDbContext db, BotSession session, CancellationToken cancellationToken)
    {
        if (session.DraftAppointmentId is not { } id) throw new InvalidOperationException("Conversation session has no draft appointment.");
        return await db.Appointments.Include(x => x.PatientProfile).SingleAsync(x => x.Id == id, cancellationToken);
    }

    private async Task SendWelcomeAsync(long chatId, bool isAdmin, CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var payment = settings.PaymentMode == PaymentMode.Deposit ? $"بیعانه {PersianFormatting.Money(settings.DepositRials)}" : PersianFormatting.Money(settings.TotalPriceRials);
        await baleClient.SendMessageAsync(chatId, $"به {_booking.OfficeName} خوش آمدید 🌿\n\nثبت درخواست هر روز فقط بین ساعت {bookingWindow.WindowLabel} انجام می‌شود. پس از تکمیل مشخصات و پرداخت {payment}، اولین زمان {settings.SlotMinutes} دقیقه‌ای خالی برای شما قطعی می‌شود.", BaleKeyboards.MainMenu(isAdmin), cancellationToken);
    }

    private async Task SendHelpAsync(long chatId, bool isAdmin, CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetAsync(cancellationToken);
        var payment = settings.PaymentMode == PaymentMode.Deposit ? $"بیعانه {PersianFormatting.Money(settings.DepositRials)} از مبلغ کل {PersianFormatting.Money(settings.TotalPriceRials)}" : $"مبلغ کامل {PersianFormatting.Money(settings.TotalPriceRials)}";
        await baleClient.SendMessageAsync(chatId, $"راهنما\n\n۱) در بازه {bookingWindow.WindowLabel} «دریافت نوبت» را بزنید.\n۲) مشخصات بیمار و بیمه را کامل کنید.\n۳) {payment} را از کیف پول بله بپردازید.\n۴) ساعت و کد پیگیری همان لحظه ارسال می‌شود.\n\nمدت هر نوبت فعلاً {settings.SlotMinutes} دقیقه است.", BaleKeyboards.MainMenu(isAdmin), cancellationToken);
    }

    private static string BuildReview(Appointment a)
    {
        var payment = a.IsDepositPayment ? $"📌 بیعانه: {PersianFormatting.Money(a.AmountRials)}\n💰 مبلغ کل: {PersianFormatting.Money(a.EffectiveTotalPriceRials)}\n💵 مانده: {PersianFormatting.Money(a.RemainingRials)}" : $"💳 مبلغ پرداخت: {PersianFormatting.Money(a.AmountRials)}";
        return $"""
            لطفاً اطلاعات را بررسی کنید:

            👤 نام بیمار: {a.PatientProfile.FullName}
            🪪 کد ملی: {a.PatientProfile.NationalCode}
            🎂 تاریخ تولد: {a.PatientProfile.BirthDate}
            📱 موبایل: {a.PatientProfile.Mobile}
            🛡 بیمه: {a.PatientProfile.InsuranceProvider}
            🔢 شماره بیمه: {a.PatientProfile.InsuranceNumber}
            📝 علت مراجعه: {a.Complaint}
            {payment}

            با تأیید، رضایت می‌دهید این اطلاعات برای نوبت‌دهی و ارائه خدمات مطب پردازش شود.
            """;
    }

    private string BuildAppointmentStatus(Appointment a)
    {
        var status = a.Status switch { AppointmentStatus.AwaitingPayment => "در انتظار پرداخت", AppointmentStatus.Confirmed => "قطعی", AppointmentStatus.PaymentNeedsReview => "پرداخت‌شده؛ نیازمند بررسی", AppointmentStatus.Cancelled => "لغوشده", AppointmentStatus.Expired => "منقضی‌شده", _ => "در حال تکمیل" };
        var time = a.Reservation is null ? "هنوز تخصیص نیافته" : PersianFormatting.DateTime(a.Reservation.StartUtc, tehranTime);
        var balance = a.IsDepositPayment ? $"\nمانده قابل پرداخت: {PersianFormatting.Money(a.RemainingRials)}" : string.Empty;
        return $"وضعیت: {status}\nزمان: {time}\nکد پیگیری: {a.TrackingCode ?? "پس از پرداخت صادر می‌شود"}{balance}";
    }

    private async Task SendLongMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        const int max = 3800;
        for (var offset = 0; offset < text.Length; offset += max)
        {
            var length = Math.Min(max, text.Length - offset);
            await baleClient.SendMessageAsync(chatId, text.Substring(offset, length), cancellationToken: cancellationToken);
        }
        await baleClient.SendMessageAsync(chatId, "پایان گزارش.", BaleKeyboards.Admin(), cancellationToken);
    }

    private static bool TryParseTomans(string input, out long rials)
    {
        rials = 0;
        var normalized = InputValidators.NormalizeDigits(input).Replace(",", string.Empty).Replace("٬", string.Empty).Replace(" ", string.Empty).Replace("تومان", string.Empty);
        return long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var tomans) && tomans > 0 && tomans <= long.MaxValue / 10 && (rials = tomans * 10) > 0;
    }

    private static bool ProfileIsComplete(PatientProfile p) => !string.IsNullOrWhiteSpace(p.FullName) && !string.IsNullOrWhiteSpace(p.NationalCode) && !string.IsNullOrWhiteSpace(p.BirthDate) && !string.IsNullOrWhiteSpace(p.Mobile) && !string.IsNullOrWhiteSpace(p.InsuranceProvider) && !string.IsNullOrWhiteSpace(p.InsuranceNumber);
    private bool IsAdmin(long userId) => _bale.AdminUserIds.Contains(userId);
    private static bool TryParseDay(string text, out DayOfWeek day)
    {
        day = text switch { "شنبه" => DayOfWeek.Saturday, "یکشنبه" => DayOfWeek.Sunday, "دوشنبه" => DayOfWeek.Monday, "سه‌شنبه" => DayOfWeek.Tuesday, "چهارشنبه" => DayOfWeek.Wednesday, "پنج‌شنبه" => DayOfWeek.Thursday, "جمعه" => DayOfWeek.Friday, _ => (DayOfWeek)(-1) };
        return day != (DayOfWeek)(-1);
    }
}
