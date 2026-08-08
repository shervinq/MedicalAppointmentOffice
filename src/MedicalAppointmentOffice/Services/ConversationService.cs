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
    IOptions<BookingOptions> bookingOptions,
    IOptions<BaleOptions> baleOptions,
    IClock clock,
    TehranTime tehranTime,
    ILogger<ConversationService> logger)
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

        if (update.Message is not { } message || message.From is null)
        {
            return;
        }

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

        if (isAdmin && await TryHandleAdminCommandAsync(userId, chatId, text, cancellationToken))
        {
            return;
        }

        var session = await GetOrCreateSessionAsync(userId, chatId, cancellationToken);
        if (text == "❌ انصراف")
        {
            await CancelDraftAndResetAsync(session, cancellationToken);
            await baleClient.SendMessageAsync(
                chatId,
                "فرایند فعلی لغو شد.",
                BaleKeyboards.Main(isAdmin),
                cancellationToken);
            return;
        }

        if (isAdmin && session.State >= ConversationState.AwaitingScheduleDay)
        {
            await HandleAdminStateAsync(session, text, cancellationToken);
            return;
        }

        switch (session.State)
        {
            case ConversationState.AwaitingFullName:
                await SaveFullNameAsync(session, text, cancellationToken);
                break;
            case ConversationState.AwaitingNationalCode:
                await SaveNationalCodeAsync(session, text, cancellationToken);
                break;
            case ConversationState.AwaitingBirthDate:
                await SaveBirthDateAsync(session, text, cancellationToken);
                break;
            case ConversationState.AwaitingMobile:
                await SaveMobileAsync(session, message, cancellationToken);
                break;
            case ConversationState.AwaitingInsuranceProvider:
                await SaveInsuranceProviderAsync(session, text, cancellationToken);
                break;
            case ConversationState.AwaitingInsuranceNumber:
                await SaveInsuranceNumberAsync(session, text, cancellationToken);
                break;
            case ConversationState.AwaitingComplaint:
                await SaveComplaintAsync(session, text, cancellationToken);
                break;
            case ConversationState.AwaitingConfirmation:
                await HandleConfirmationAsync(session, text, cancellationToken);
                break;
            default:
                await HandleMainMenuAsync(userId, chatId, text, isAdmin, cancellationToken);
                break;
        }
    }

    private async Task HandleMainMenuAsync(
        long userId,
        long chatId,
        string text,
        bool isAdmin,
        CancellationToken cancellationToken)
    {
        switch (text)
        {
            case "🩺 دریافت نوبت":
                await StartBookingAsync(userId, chatId, cancellationToken);
                break;
            case "📌 پیگیری نوبت":
                await TrackAppointmentAsync(userId, chatId, cancellationToken);
                break;
            case "ℹ️ راهنما":
                await SendHelpAsync(chatId, isAdmin, cancellationToken);
                break;
            case "⚙️ مدیریت مطب" when isAdmin:
                await ShowAdminMenuAsync(userId, chatId, cancellationToken);
                break;
            default:
                await SendWelcomeAsync(chatId, isAdmin, cancellationToken);
                break;
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
                        (x.Status == AppointmentStatus.Confirmed ||
                         x.Status == AppointmentStatus.AwaitingPayment ||
                         x.Status == AppointmentStatus.PaymentNeedsReview))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing?.Status == AppointmentStatus.AwaitingPayment &&
            existing.InvoiceSentAtUtc < now.AddMinutes(-_booking.SessionMinutes))
        {
            existing.Status = AppointmentStatus.Expired;
            if (existing.Reservation is not null)
            {
                db.Reservations.Remove(existing.Reservation);
            }

            await db.SaveChangesAsync(cancellationToken);
            existing = null;
        }

        if (existing?.Status == AppointmentStatus.Confirmed &&
            existing.Reservation is { StartUtc: var start } && start > now)
        {
            await baleClient.SendMessageAsync(
                chatId,
                $"شما یک نوبت فعال دارید:\n{BuildAppointmentStatus(existing)}",
                BaleKeyboards.Main(IsAdmin(userId)),
                cancellationToken);
            return;
        }

        if (existing?.Status == AppointmentStatus.PaymentNeedsReview)
        {
            await baleClient.SendMessageAsync(
                chatId,
                "پرداخت قبلی شما ثبت شده و در حال بررسی ادمین است؛ تا اعلام نتیجه نوبت جدید ثبت نکنید.",
                cancellationToken: cancellationToken);
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
            await baleClient.SendMessageAsync(
                chatId,
                $"⏰ ثبت درخواست فقط هر روز از ساعت {bookingWindow.WindowLabel} باز است.\nنوبت بعدی بازشدن: {PersianFormatting.DateTime(next, tehranTime)}",
                BaleKeyboards.Main(IsAdmin(userId)),
                cancellationToken);
            return;
        }

        var patient = await db.Patients.SingleOrDefaultAsync(x => x.BaleUserId == userId, cancellationToken);
        if (patient is null)
        {
            patient = new PatientProfile
            {
                BaleUserId = userId,
                ChatId = chatId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Patients.Add(patient);
        }
        else
        {
            patient.ChatId = chatId;
        }

        var appointment = new Appointment
        {
            PatientProfile = patient,
            AmountRials = _booking.PriceRials,
            InvoicePayload = $"appointment:{Guid.NewGuid():N}",
            CreatedAtUtc = now
        };
        db.Appointments.Add(appointment);

        session.DraftAppointmentId = appointment.Id;
        session.EntryGrantedUntilUtc = now.AddMinutes(_booking.SessionMinutes);
        session.State = ProfileIsComplete(patient)
            ? ConversationState.AwaitingComplaint
            : ConversationState.AwaitingFullName;
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
            await baleClient.SendMessageAsync(session.ChatId, "نام و نام خانوادگی کامل را وارد کنید (حداقل دو بخش).", cancellationToken: cancellationToken);
            return;
        }

        await UpdatePatientAndSessionAsync(session, patient => patient.FullName = text, ConversationState.AwaitingNationalCode, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, "کد ملی ۱۰ رقمی بیمار را وارد کنید.", cancellationToken: cancellationToken);
    }

    private async Task SaveNationalCodeAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        var nationalCode = InputValidators.NormalizeDigits(text).Replace("-", string.Empty, StringComparison.Ordinal);
        if (!InputValidators.IsValidIranianNationalCode(nationalCode))
        {
            await baleClient.SendMessageAsync(session.ChatId, "کد ملی معتبر نیست؛ لطفاً ۱۰ رقم را دوباره بررسی کنید.", cancellationToken: cancellationToken);
            return;
        }

        await UpdatePatientAndSessionAsync(session, patient => patient.NationalCode = nationalCode, ConversationState.AwaitingBirthDate, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, "تاریخ تولد بیمار را وارد کنید؛ مثال: ۱۳۷۰/۰۵/۲۱", cancellationToken: cancellationToken);
    }

    private async Task SaveBirthDateAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (!InputValidators.TryParsePersianOrGregorianDate(text, out var birthDate) ||
            birthDate > DateOnly.FromDateTime(tehranTime.ToLocal(clock.UtcNow).DateTime) ||
            birthDate.Year < 1900)
        {
            await baleClient.SendMessageAsync(session.ChatId, "فرمت تاریخ معتبر نیست؛ مانند ۱۳۷۰/۰۵/۲۱ وارد کنید.", cancellationToken: cancellationToken);
            return;
        }

        var normalized = InputValidators.NormalizeDigits(text).Replace('-', '/');
        await UpdatePatientAndSessionAsync(session, patient => patient.BirthDate = normalized, ConversationState.AwaitingMobile, cancellationToken);
        await baleClient.SendMessageAsync(
            session.ChatId,
            "شماره موبایل بیمار را وارد کنید یا دکمه ارسال شماره را بزنید.",
            BaleKeyboards.Contact(),
            cancellationToken);
    }

    private async Task SaveMobileAsync(BotSession session, BaleMessage message, CancellationToken cancellationToken)
    {
        if (message.Contact?.UserId is { } contactUserId && contactUserId != session.BaleUserId)
        {
            await baleClient.SendMessageAsync(session.ChatId, "لطفاً شماره متعلق به حساب خودتان یا شماره بیمار را به‌صورت متنی ارسال کنید.", cancellationToken: cancellationToken);
            return;
        }

        var mobile = InputValidators.NormalizeMobile(message.Contact?.PhoneNumber ?? message.Text);
        if (string.IsNullOrEmpty(mobile))
        {
            await baleClient.SendMessageAsync(session.ChatId, "شماره معتبر باید ۱۱ رقم و با ۰۹ شروع شود.", BaleKeyboards.Contact(), cancellationToken);
            return;
        }

        await UpdatePatientAndSessionAsync(session, patient => patient.Mobile = mobile, ConversationState.AwaitingInsuranceProvider, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, "نام بیمه را وارد کنید؛ مانند تأمین اجتماعی. اگر بیمه ندارید بنویسید «بدون بیمه».", cancellationToken: cancellationToken);
    }

    private async Task SaveInsuranceProviderAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (text.Length is < 2 or > 80)
        {
            await baleClient.SendMessageAsync(session.ChatId, "نام بیمه معتبر نیست.", cancellationToken: cancellationToken);
            return;
        }

        await UpdatePatientAndSessionAsync(session, patient => patient.InsuranceProvider = text, ConversationState.AwaitingInsuranceNumber, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, "شماره بیمه/شماره دفترچه را وارد کنید؛ اگر ندارید بنویسید «ندارد».", cancellationToken: cancellationToken);
    }

    private async Task SaveInsuranceNumberAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (text.Length is < 2 or > 80)
        {
            await baleClient.SendMessageAsync(session.ChatId, "شماره بیمه معتبر نیست.", cancellationToken: cancellationToken);
            return;
        }

        await UpdatePatientAndSessionAsync(session, patient => patient.InsuranceNumber = InputValidators.NormalizeDigits(text), ConversationState.AwaitingComplaint, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, "علت مراجعه یا توضیح کوتاه برای پزشک را بنویسید.", cancellationToken: cancellationToken);
    }

    private async Task SaveComplaintAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (text.Length is < 3 or > 500)
        {
            await baleClient.SendMessageAsync(session.ChatId, "توضیح مراجعه باید بین ۳ تا ۵۰۰ کاراکتر باشد.", cancellationToken: cancellationToken);
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await GetDraftAsync(db, session, cancellationToken);
        appointment.Complaint = text;
        var trackedSession = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        trackedSession.State = ConversationState.AwaitingConfirmation;
        trackedSession.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await baleClient.SendMessageAsync(
            session.ChatId,
            BuildReview(appointment),
            BaleKeyboards.Confirmation(),
            cancellationToken);
    }

    private async Task HandleConfirmationAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (text == "✏️ ویرایش اطلاعات")
        {
            await using var editDb = await contextFactory.CreateDbContextAsync(cancellationToken);
            var tracked = await editDb.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
            tracked.State = ConversationState.AwaitingFullName;
            tracked.UpdatedAtUtc = clock.UtcNow;
            await editDb.SaveChangesAsync(cancellationToken);
            await baleClient.SendMessageAsync(session.ChatId, "نام و نام خانوادگی بیمار را دوباره وارد کنید.", cancellationToken: cancellationToken);
            return;
        }

        if (text != "✅ تأیید و پرداخت")
        {
            await baleClient.SendMessageAsync(session.ChatId, "یکی از دکمه‌های تأیید، ویرایش یا انصراف را انتخاب کنید.", BaleKeyboards.Confirmation(), cancellationToken);
            return;
        }

        if (session.EntryGrantedUntilUtc < clock.UtcNow)
        {
            await CancelDraftAndResetAsync(session, cancellationToken);
            await baleClient.SendMessageAsync(
                session.ChatId,
                $"مهلت تکمیل فرم تمام شد. ثبت جدید فردا در بازه {bookingWindow.WindowLabel} امکان‌پذیر است.",
                BaleKeyboards.Main(IsAdmin(session.BaleUserId)),
                cancellationToken);
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await GetDraftAsync(db, session, cancellationToken);
        appointment.Status = AppointmentStatus.AwaitingPayment;
        appointment.InvoiceSentAtUtc = clock.UtcNow;
        var trackedSession = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        trackedSession.State = ConversationState.Idle;
        trackedSession.DraftAppointmentId = null;
        await db.SaveChangesAsync(cancellationToken);

        await baleClient.SendMessageAsync(
            session.ChatId,
            $"صورتحساب {PersianFormatting.Money(appointment.AmountRials)} ارسال می‌شود. ساعت دقیق از اولین زمان خالی و به‌ترتیب پرداخت قطعی خواهد شد.",
            cancellationToken: cancellationToken);
        await baleClient.SendInvoiceAsync(session.ChatId, appointment.InvoicePayload, appointment.AmountRials, cancellationToken);
    }

    private async Task TrackAppointmentAsync(long userId, long chatId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await db.Appointments
            .AsNoTracking()
            .Include(x => x.PatientProfile)
            .Include(x => x.Reservation)
            .Where(x => x.PatientProfile.BaleUserId == userId && x.Status != AppointmentStatus.Draft)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var text = appointment is null
            ? "هنوز نوبتی برای شما ثبت نشده است."
            : BuildAppointmentStatus(appointment);
        await baleClient.SendMessageAsync(chatId, text, BaleKeyboards.Main(IsAdmin(userId)), cancellationToken);
    }

    private async Task<bool> TryHandleAdminCommandAsync(
        long userId,
        long chatId,
        string text,
        CancellationToken cancellationToken)
    {
        if (text is "/admin" or "⚙️ مدیریت مطب" or "🔙 منوی مدیریت")
        {
            await ShowAdminMenuAsync(userId, chatId, cancellationToken);
            return true;
        }

        if (text == "🔙 منوی اصلی")
        {
            await ResetSessionAsync(userId, chatId, cancellationToken);
            await SendWelcomeAsync(chatId, true, cancellationToken);
            return true;
        }

        if (text == "📅 ساعات هفتگی")
        {
            var session = await GetOrCreateSessionAsync(userId, chatId, cancellationToken);
            await SendScheduleAsync(session, cancellationToken);
            return true;
        }

        if (text == "🚫 تعطیلی تاریخ")
        {
            var session = await GetOrCreateSessionAsync(userId, chatId, cancellationToken);
            await UpdateSessionStateAsync(session, ConversationState.AwaitingClosedDate, null, cancellationToken);
            await baleClient.SendMessageAsync(
                chatId,
                "تاریخ تعطیلی را به‌شکل شمسی وارد کنید؛ مثال: ۱۴۰۵/۰۵/۲۱",
                cancellationToken: cancellationToken);
            return true;
        }

        if (text == "📋 نوبت‌های امروز")
        {
            await ShowTodayAppointmentsAsync(chatId, cancellationToken);
            return true;
        }

        if (text == "📊 آمار")
        {
            await ShowStatsAsync(chatId, cancellationToken);
            return true;
        }

        if (text.StartsWith("/cancel ", StringComparison.OrdinalIgnoreCase))
        {
            await CancelAppointmentByAdminAsync(chatId, text[8..].Trim(), cancellationToken);
            return true;
        }

        if (text.StartsWith("/open ", StringComparison.OrdinalIgnoreCase))
        {
            await OpenDateAsync(chatId, text[6..].Trim(), cancellationToken);
            return true;
        }

        return false;
    }

    private async Task ShowAdminMenuAsync(long userId, long chatId, CancellationToken cancellationToken)
    {
        var session = await GetOrCreateSessionAsync(userId, chatId, cancellationToken);
        await UpdateSessionStateAsync(session, ConversationState.Idle, null, cancellationToken);
        await baleClient.SendMessageAsync(
            chatId,
            "پنل مدیریت مطب\n\nبرای لغو نوبت نیز می‌توانید بنویسید:\n/cancel GZ-XXXXXXXX علت لغو",
            BaleKeyboards.Admin(),
            cancellationToken);
    }

    private async Task HandleAdminStateAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (session.State == ConversationState.AwaitingScheduleDay)
        {
            if (!TryParseDay(text, out var day))
            {
                await baleClient.SendMessageAsync(session.ChatId, "یکی از روزهای هفته را انتخاب کنید.", BaleKeyboards.WeekDays(), cancellationToken);
                return;
            }

            await UpdateSessionStateAsync(session, ConversationState.AwaitingScheduleRange, ((int)day).ToString(CultureInfo.InvariantCulture), cancellationToken);
            await baleClient.SendMessageAsync(
                session.ChatId,
                $"ساعت کاری {PersianFormatting.DayName(day)} را مانند 17:00-01:00 وارد کنید؛ یا بنویسید «تعطیل».",
                cancellationToken: cancellationToken);
            return;
        }

        if (session.State == ConversationState.AwaitingScheduleRange)
        {
            await SaveScheduleRangeAsync(session, text, cancellationToken);
            return;
        }

        if (session.State == ConversationState.AwaitingClosedDate)
        {
            await CloseDateAsync(session, text, cancellationToken);
        }
    }

    private async Task SaveScheduleRangeAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (!int.TryParse(session.Context, CultureInfo.InvariantCulture, out var dayNumber) ||
            !Enum.IsDefined(typeof(DayOfWeek), dayNumber))
        {
            await ShowAdminMenuAsync(session.BaleUserId, session.ChatId, cancellationToken);
            return;
        }

        var day = (DayOfWeek)dayNumber;
        var isClosed = text is "تعطیل" or "بسته";
        if (!isClosed && !InputValidators.TryParseClockRange(text, out var start, out var end))
        {
            await baleClient.SendMessageAsync(session.ChatId, "فرمت صحیح نیست؛ نمونه: 17:00-01:00 یا «تعطیل».", cancellationToken: cancellationToken);
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var schedule = await db.WeeklySchedules.SingleAsync(x => x.DayOfWeek == day, cancellationToken);
        schedule.IsEnabled = !isClosed;
        if (!isClosed)
        {
            schedule.StartMinute = start;
            schedule.EndMinute = end;
        }

        var trackedSession = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        trackedSession.State = ConversationState.AwaitingScheduleDay;
        trackedSession.Context = null;
        await db.SaveChangesAsync(cancellationToken);

        var result = isClosed ? "تعطیل" : $"{PersianFormatting.Clock(start)} تا {PersianFormatting.Clock(end)}";
        await baleClient.SendMessageAsync(
            session.ChatId,
            $"✅ برنامه {PersianFormatting.DayName(day)} روی «{result}» تنظیم شد. روز دیگری را انتخاب کنید.",
            BaleKeyboards.WeekDays(),
            cancellationToken);
    }

    private async Task CloseDateAsync(BotSession session, string text, CancellationToken cancellationToken)
    {
        if (!InputValidators.TryParsePersianOrGregorianDate(text, out var date))
        {
            await baleClient.SendMessageAsync(session.ChatId, "تاریخ معتبر نیست؛ نمونه شمسی: ۱۴۰۵/۰۵/۲۱", cancellationToken: cancellationToken);
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var exception = await db.ScheduleExceptions.SingleOrDefaultAsync(x => x.LocalDate == date, cancellationToken);
        if (exception is null)
        {
            exception = new ScheduleException { LocalDate = date };
            db.ScheduleExceptions.Add(exception);
        }

        exception.IsClosed = true;
        exception.Note = "تعطیلی ثبت‌شده توسط ادمین";
        var trackedSession = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        trackedSession.State = ConversationState.Idle;
        await db.SaveChangesAsync(cancellationToken);

        await baleClient.SendMessageAsync(
            session.ChatId,
            $"✅ تاریخ {PersianFormatting.Date(date)} تعطیل شد. برای بازکردن دوباره بنویسید: /open {PersianFormatting.Date(date)}",
            BaleKeyboards.Admin(),
            cancellationToken);
    }

    private async Task OpenDateAsync(long chatId, string input, CancellationToken cancellationToken)
    {
        if (!InputValidators.TryParsePersianOrGregorianDate(input, out var date))
        {
            await baleClient.SendMessageAsync(chatId, "تاریخ معتبر نیست.", cancellationToken: cancellationToken);
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var exception = await db.ScheduleExceptions.SingleOrDefaultAsync(x => x.LocalDate == date, cancellationToken);
        if (exception is not null)
        {
            db.ScheduleExceptions.Remove(exception);
            await db.SaveChangesAsync(cancellationToken);
        }

        await baleClient.SendMessageAsync(chatId, $"تاریخ {PersianFormatting.Date(date)} مطابق برنامه هفتگی باز شد.", BaleKeyboards.Admin(), cancellationToken);
    }

    private async Task CancelAppointmentByAdminAsync(long chatId, string arguments, CancellationToken cancellationToken)
    {
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            await baleClient.SendMessageAsync(chatId, "نمونه: /cancel GZ-12345678 علت لغو", cancellationToken: cancellationToken);
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var code = parts[0].ToUpperInvariant();
        var appointment = await db.Appointments
            .Include(x => x.PatientProfile)
            .Include(x => x.Reservation)
            .SingleOrDefaultAsync(x => x.TrackingCode == code, cancellationToken);
        if (appointment is null)
        {
            await baleClient.SendMessageAsync(chatId, "کد پیگیری پیدا نشد.", cancellationToken: cancellationToken);
            return;
        }

        appointment.Status = AppointmentStatus.Cancelled;
        appointment.CancelledAtUtc = clock.UtcNow;
        appointment.CancellationReason = parts.Length == 2 ? parts[1] : "لغو توسط مطب";
        if (appointment.Reservation is not null)
        {
            db.Reservations.Remove(appointment.Reservation);
        }

        await db.SaveChangesAsync(cancellationToken);
        await baleClient.SendMessageAsync(
            appointment.PatientProfile.ChatId,
            $"نوبت {appointment.TrackingCode} توسط مطب لغو شد.\nعلت: {appointment.CancellationReason}\nبرای هماهنگی بازپرداخت با مطب تماس بگیرید.",
            cancellationToken: cancellationToken);
        await baleClient.SendMessageAsync(chatId, "نوبت لغو و زمان آن آزاد شد. بازپرداخت وجه باید طبق روال مطب انجام شود.", BaleKeyboards.Admin(), cancellationToken);
    }

    private async Task ShowTodayAppointmentsAsync(long chatId, CancellationToken cancellationToken)
    {
        var localNow = tehranTime.ToLocal(clock.UtcNow);
        var date = DateOnly.FromDateTime(localNow.DateTime);
        var start = tehranTime.ToUtc(date, 0);
        var end = tehranTime.ToUtc(date.AddDays(1), 0);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var appointments = await db.Appointments
            .AsNoTracking()
            .Include(x => x.PatientProfile)
            .Include(x => x.Reservation)
            .Where(x => x.Status == AppointmentStatus.Confirmed &&
                        x.Reservation != null &&
                        x.Reservation.StartUtc >= start && x.Reservation.StartUtc < end)
            .OrderBy(x => x.Reservation!.StartUtc)
            .ToListAsync(cancellationToken);

        if (appointments.Count == 0)
        {
            await baleClient.SendMessageAsync(chatId, "برای امروز نوبت قطعی ثبت نشده است.", BaleKeyboards.Admin(), cancellationToken);
            return;
        }

        var builder = new StringBuilder("📋 نوبت‌های امروز\n\n");
        foreach (var appointment in appointments)
        {
            builder.Append(tehranTime.ToLocal(appointment.Reservation!.StartUtc).ToString("HH:mm", CultureInfo.InvariantCulture))
                .Append(" — ")
                .Append(appointment.PatientProfile.FullName)
                .Append(" — ")
                .Append(appointment.TrackingCode)
                .AppendLine();
        }

        await baleClient.SendMessageAsync(chatId, builder.ToString(), BaleKeyboards.Admin(), cancellationToken);
    }

    private async Task ShowStatsAsync(long chatId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var confirmed = await db.Appointments.CountAsync(x => x.Status == AppointmentStatus.Confirmed, cancellationToken);
        var pending = await db.Appointments.CountAsync(x => x.Status == AppointmentStatus.AwaitingPayment, cancellationToken);
        var needsReview = await db.Appointments.CountAsync(x => x.Status == AppointmentStatus.PaymentNeedsReview, cancellationToken);
        var revenue = await db.Appointments
            .Where(x => x.Status == AppointmentStatus.Confirmed)
            .SumAsync(x => (long?)x.AmountRials, cancellationToken) ?? 0;

        await baleClient.SendMessageAsync(
            chatId,
            $"📊 آمار کل\n\nنوبت قطعی: {confirmed:N0}\nدر انتظار پرداخت: {pending:N0}\nنیازمند بررسی: {needsReview:N0}\nمبلغ پرداخت‌های قطعی: {PersianFormatting.Money(revenue)}",
            BaleKeyboards.Admin(),
            cancellationToken);
    }

    private async Task SendScheduleAsync(BotSession session, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var schedules = await db.WeeklySchedules.AsNoTracking().OrderBy(x => ((int)x.DayOfWeek + 1) % 7).ToListAsync(cancellationToken);
        var builder = new StringBuilder("📅 ساعات هفتگی فعلی\n\n");
        foreach (var schedule in schedules)
        {
            var value = schedule.IsEnabled
                ? $"{PersianFormatting.Clock(schedule.StartMinute)} تا {PersianFormatting.Clock(schedule.EndMinute)}"
                : "تعطیل";
            builder.Append(PersianFormatting.DayName(schedule.DayOfWeek)).Append(": ").AppendLine(value);
        }

        builder.Append("\nبرای تغییر، روز را انتخاب کنید.");
        await UpdateSessionStateAsync(session, ConversationState.AwaitingScheduleDay, null, cancellationToken);
        await baleClient.SendMessageAsync(session.ChatId, builder.ToString(), BaleKeyboards.WeekDays(), cancellationToken);
    }

    private async Task UpdatePatientAndSessionAsync(
        BotSession session,
        Action<PatientProfile> update,
        ConversationState nextState,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var patient = await db.Patients.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        update(patient);
        patient.UpdatedAtUtc = clock.UtcNow;
        var trackedSession = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        trackedSession.State = nextState;
        trackedSession.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<BotSession> GetOrCreateSessionAsync(long userId, long chatId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.BotSessions.SingleOrDefaultAsync(x => x.BaleUserId == userId, cancellationToken);
        if (session is null)
        {
            session = new BotSession
            {
                BaleUserId = userId,
                ChatId = chatId,
                State = ConversationState.Idle,
                UpdatedAtUtc = clock.UtcNow
            };
            db.BotSessions.Add(session);
        }
        else
        {
            session.ChatId = chatId;
        }

        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    private async Task ResetSessionAsync(long userId, long chatId, CancellationToken cancellationToken)
    {
        var session = await GetOrCreateSessionAsync(userId, chatId, cancellationToken);
        await UpdateSessionStateAsync(session, ConversationState.Idle, null, cancellationToken);
    }

    private async Task UpdateSessionStateAsync(
        BotSession session,
        ConversationState state,
        string? context,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tracked = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        tracked.State = state;
        tracked.Context = context;
        tracked.UpdatedAtUtc = clock.UtcNow;
        if (state == ConversationState.Idle)
        {
            tracked.DraftAppointmentId = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CancelDraftAndResetAsync(BotSession session, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tracked = await db.BotSessions.SingleAsync(x => x.BaleUserId == session.BaleUserId, cancellationToken);
        if (tracked.DraftAppointmentId is { } draftId)
        {
            var appointment = await db.Appointments.Include(x => x.Reservation).SingleOrDefaultAsync(x => x.Id == draftId, cancellationToken);
            if (appointment is not null && appointment.Status == AppointmentStatus.Draft)
            {
                appointment.Status = AppointmentStatus.Cancelled;
                appointment.CancelledAtUtc = clock.UtcNow;
                appointment.CancellationReason = "لغو توسط کاربر پیش از پرداخت";
            }
        }

        tracked.State = ConversationState.Idle;
        tracked.DraftAppointmentId = null;
        tracked.Context = null;
        tracked.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Appointment> GetDraftAsync(
        AppDbContext db,
        BotSession session,
        CancellationToken cancellationToken)
    {
        if (session.DraftAppointmentId is not { } appointmentId)
        {
            throw new InvalidOperationException("Conversation session has no draft appointment.");
        }

        return await db.Appointments
            .Include(x => x.PatientProfile)
            .SingleAsync(x => x.Id == appointmentId, cancellationToken);
    }

    private async Task SendWelcomeAsync(long chatId, bool isAdmin, CancellationToken cancellationToken)
    {
        await baleClient.SendMessageAsync(
            chatId,
            $"به {_booking.OfficeName} خوش آمدید 🌿\n\nثبت درخواست هر روز فقط بین ساعت {bookingWindow.WindowLabel} انجام می‌شود. پس از تکمیل مشخصات و پرداخت {PersianFormatting.Money(_booking.PriceRials)}، اولین زمان ۱۵ دقیقه‌ای خالی برای شما قطعی می‌شود.",
            BaleKeyboards.Main(isAdmin),
            cancellationToken);
    }

    private async Task SendHelpAsync(long chatId, bool isAdmin, CancellationToken cancellationToken)
    {
        await baleClient.SendMessageAsync(
            chatId,
            "راهنما\n\n۱) در بازه ۱۴:۰۰ تا ۱۴:۳۰ «دریافت نوبت» را بزنید.\n۲) مشخصات بیمار و بیمه را کامل کنید.\n۳) صورتحساب کیف پول بله را بپردازید.\n۴) ساعت و کد پیگیری همان لحظه ارسال می‌شود.\n\nاطلاعات پزشکی شما فقط برای ارائه خدمت مطب نگهداری می‌شود. برای لغو یا بازپرداخت با مطب تماس بگیرید.",
            BaleKeyboards.Main(isAdmin),
            cancellationToken);
    }

    private string BuildReview(Appointment appointment) => $"""
        لطفاً اطلاعات را بررسی کنید:

        👤 نام بیمار: {appointment.PatientProfile.FullName}
        🪪 کد ملی: {appointment.PatientProfile.NationalCode}
        🎂 تاریخ تولد: {appointment.PatientProfile.BirthDate}
        📱 موبایل: {appointment.PatientProfile.Mobile}
        🛡 بیمه: {appointment.PatientProfile.InsuranceProvider}
        🔢 شماره بیمه: {appointment.PatientProfile.InsuranceNumber}
        📝 علت مراجعه: {appointment.Complaint}
        💳 مبلغ: {PersianFormatting.Money(appointment.AmountRials)}

        با تأیید، رضایت می‌دهید این اطلاعات برای نوبت‌دهی و ارائه خدمات مطب پردازش شود.
        """;

    private string BuildAppointmentStatus(Appointment appointment)
    {
        var status = appointment.Status switch
        {
            AppointmentStatus.AwaitingPayment => "در انتظار پرداخت",
            AppointmentStatus.Confirmed => "قطعی",
            AppointmentStatus.PaymentNeedsReview => "پرداخت‌شده؛ نیازمند بررسی",
            AppointmentStatus.Cancelled => "لغوشده",
            AppointmentStatus.Expired => "منقضی‌شده",
            _ => "در حال تکمیل"
        };
        var time = appointment.Reservation is null
            ? "هنوز تخصیص نیافته"
            : PersianFormatting.DateTime(appointment.Reservation.StartUtc, tehranTime);
        return $"وضعیت: {status}\nزمان: {time}\nکد پیگیری: {appointment.TrackingCode ?? "پس از پرداخت صادر می‌شود"}";
    }

    private static bool ProfileIsComplete(PatientProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.FullName) &&
        !string.IsNullOrWhiteSpace(profile.NationalCode) &&
        !string.IsNullOrWhiteSpace(profile.BirthDate) &&
        !string.IsNullOrWhiteSpace(profile.Mobile) &&
        !string.IsNullOrWhiteSpace(profile.InsuranceProvider) &&
        !string.IsNullOrWhiteSpace(profile.InsuranceNumber);

    private bool IsAdmin(long userId) => _bale.AdminUserIds.Contains(userId);

    private static bool TryParseDay(string text, out DayOfWeek day)
    {
        day = text switch
        {
            "شنبه" => DayOfWeek.Saturday,
            "یکشنبه" => DayOfWeek.Sunday,
            "دوشنبه" => DayOfWeek.Monday,
            "سه‌شنبه" => DayOfWeek.Tuesday,
            "چهارشنبه" => DayOfWeek.Wednesday,
            "پنج‌شنبه" => DayOfWeek.Thursday,
            "جمعه" => DayOfWeek.Friday,
            _ => (DayOfWeek)(-1)
        };
        return day != (DayOfWeek)(-1);
    }
}
