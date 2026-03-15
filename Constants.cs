namespace InvoiceSchedulerJob;

/// <summary>
/// Константы статусов и типов, используемых в job.
/// </summary>
public static class Constants
{
    public static class InvoiceStatus
    {
        public const string Actual = "actual";
        public const string Closed = "closed";
        public const string PeriodicityOneTime = "oneTime";
        public const string PeriodicityAny = "any";
    }

    public static class PaymentStatus
    {
        public const string NonPaid = "non_paid";
        public const string Paid = "paid";
    }

    public static class TransactionType
    {
        public const string Credit = "credit";
        public const string StatusSuccess = "success";
    }

    public static class BillingType
    {
        public const string Subscription = "subscription";
    }

    public static class NotificationChannel
    {
        public const string Email = "email";
        public const string Telegram = "telegram";
        public const string WhatsApp = "whatsapp";
    }

    /// <summary>
    /// Тип уведомления — для контроля дублей и логики отправки.
    /// </summary>
    public static class NotificationType
    {
        public const string InsufficientBalance = "insufficient_balance";
        public const string PaymentReminder1Day = "payment_reminder_1_day";
        public const string PaymentReminder3Days = "payment_reminder_3_days";
        public const string OverdueDay1 = "overdue_day_1";
        public const string OverdueDay3 = "overdue_day_3";
        public const string OverdueDay7 = "overdue_day_7";
        public const string AutoPaymentSuccess = "auto_payment_success";
        public const string SubscriptionEndingIn3Days = "subscription_ending_in_3_days";
        public const string SubscriptionEndingSoon = "subscription_ending_soon";
        public const string SubscriptionReminderPayment = "subscription_reminder_payment";
        public const string SubscriptionExpired = "subscription_expired";
    }

    /// <summary>
    /// За сколько дней до срока оплаты создавать напоминания (1 и 3 дня).
    /// </summary>
    public static readonly int[] PaymentReminderDaysBefore = { 1, 3 };

    /// <summary>
    /// Статус записи в очереди уведомлений. Отправитель должен проверять актуальность и выставлять Cancelled, если платёж уже закрыт.
    /// </summary>
    public static class NotificationStatus
    {
        public const string New = "new";
        public const string Cancelled = "cancelled";
    }

    /// <summary>
    /// Дни просрочки, в которые создаётся уведомление (1-й, 3-й, 7-й день).
    /// </summary>
    public static readonly int[] OverdueNotificationDays = { 1, 3, 7 };
}
