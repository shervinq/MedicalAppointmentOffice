namespace MedicalAppointmentOffice.Bale;

public static class BaleKeyboards
{
    public static ReplyKeyboard Main(bool isAdmin)
    {
        var rows = new List<IReadOnlyList<KeyboardButton>>
        {
            new[] { new KeyboardButton("🩺 دریافت نوبت") },
            new[]
            {
                new KeyboardButton("📌 پیگیری نوبت"),
                new KeyboardButton("ℹ️ راهنما")
            }
        };

        if (isAdmin)
        {
            rows.Add(new[] { new KeyboardButton("⚙️ مدیریت مطب") });
        }

        return new ReplyKeyboard(rows);
    }

    public static ReplyKeyboard Contact() => new(
        new[]
        {
            new[] { new KeyboardButton("📱 ارسال شماره من", RequestContact: true) },
            new[] { new KeyboardButton("❌ انصراف") }
        });

    public static ReplyKeyboard Confirmation() => new(
        new[]
        {
            new[] { new KeyboardButton("✅ تأیید و پرداخت") },
            new[]
            {
                new KeyboardButton("✏️ ویرایش اطلاعات"),
                new KeyboardButton("❌ انصراف")
            }
        });

    public static ReplyKeyboard Admin() => new(
        new[]
        {
            new[] { new KeyboardButton("📅 ساعات هفتگی") },
            new[]
            {
                new KeyboardButton("🚫 تعطیلی تاریخ"),
                new KeyboardButton("📋 نوبت‌های امروز")
            },
            new[]
            {
                new KeyboardButton("📊 آمار"),
                new KeyboardButton("🔙 منوی اصلی")
            }
        });

    public static ReplyKeyboard WeekDays() => new(
        new[]
        {
            new[] { new KeyboardButton("شنبه"), new KeyboardButton("یکشنبه") },
            new[] { new KeyboardButton("دوشنبه"), new KeyboardButton("سه‌شنبه") },
            new[] { new KeyboardButton("چهارشنبه"), new KeyboardButton("پنج‌شنبه") },
            new[] { new KeyboardButton("جمعه"), new KeyboardButton("🔙 منوی مدیریت") }
        });
}
