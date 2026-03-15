using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using InvoiceSchedulerJob.Models.DBModels;
using InvoiceSchedulerJob.Helpers;
using static InvoiceSchedulerJob.Constants;

namespace InvoiceSchedulerJob.Services;

/// <summary>
/// Создание уведомлений в БД с типом события и привязкой к платежу/организации. Проверка дублей перед вставкой.
/// Перед отправкой сервис-отправитель должен проверить актуальность (например, оплачен ли платёж) и помечать уведомление как Cancelled при необходимости.
/// </summary>
public class NotificationService
{
    private readonly AppDbContext _db;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(AppDbContext db, ILogger<NotificationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private static Models.DBModels.Notification BuildNotification(
        string notificationType,
        string? clientId,
        string? invoiceId,
        string? invoicePaymentId,
        string? organizationId,
        string channel,
        string contactInfo,
        string subject,
        string message)
    {
        return new Models.DBModels.Notification
        {
            Id = Guid.NewGuid().ToString(),
            NotificationType = notificationType,
            ClientId = clientId,
            InvoiceId = invoiceId,
            InvoicePaymentId = invoicePaymentId,
            OrganizationId = organizationId,
            Channel = channel,
            ContactInfo = contactInfo,
            Subject = subject,
            Message = message,
            Status = NotificationStatus.New,
            CreatedAt = ParsersHelper.NowForTimestamp()
        };
    }

    /// <summary>
    /// Есть ли уже уведомление такого типа по этому платежу и каналу (защита от дублей).
    /// </summary>
    public async Task<bool> ExistsDuplicateForPaymentAsync(string notificationType, string invoicePaymentId, string channel, CancellationToken ct = default)
    {
        return await _db.Notifications
            .AnyAsync(n => n.NotificationType == notificationType && n.InvoicePaymentId == invoicePaymentId && n.Channel == channel, ct);
    }

    /// <summary>
    /// Есть ли уже уведомление такого типа по этой организации и каналу.
    /// </summary>
    public async Task<bool> ExistsDuplicateForOrganizationAsync(string notificationType, string organizationId, string channel, CancellationToken ct = default)
    {
        return await _db.Notifications
            .AnyAsync(n => n.NotificationType == notificationType && n.OrganizationId == organizationId && n.Channel == channel, ct);
    }

    /// <summary>
    /// Создаёт уведомления по платежу только по каналам, где ещё нет дубля (тип + платёж + канал).
    /// </summary>
    public async Task TryCreatePaymentNotificationAsync(
        string notificationType,
        OrganizationClient client,
        Invoice invoice,
        InvoicePayment payment,
        string subject,
        string message,
        CancellationToken ct = default)
    {
        var channels = new[]
        {
            (NotificationChannel.Email, client.ClientEmail),
            (NotificationChannel.Telegram, client.ClientTg),
            (NotificationChannel.WhatsApp, client.ClientWa)
        };
        var toAdd = new List<Models.DBModels.Notification>();
        foreach (var (channel, contact) in channels)
        {
            if (string.IsNullOrWhiteSpace(contact)) continue;
            if (await ExistsDuplicateForPaymentAsync(notificationType, payment.Id, channel, ct)) continue;
            toAdd.Add(BuildNotification(notificationType, client.Id, invoice.Id, payment.Id, null, channel, contact, subject, message));
        }
        if (toAdd.Count == 0) return;
        _db.Notifications.AddRange(toAdd);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Создано {Count} уведомлений типа {Type} для платежа {PaymentId}", toAdd.Count, notificationType, payment.Id);
    }

    /// <summary>
    /// Создаёт уведомления по организации только по каналам, где ещё нет дубля (тип + организация + канал).
    /// </summary>
    public async Task TryCreateSubscriptionNotificationAsync(
        string notificationType,
        Organization organization,
        string subject,
        string message,
        CancellationToken ct = default)
    {
        if (await ExistsDuplicateForOrganizationAsync(notificationType, organization.Id, NotificationChannel.Email, ct))
            return;
        var email = await _db.Users
            .Where(u => u.Organization == organization.Id && (u.Isdeleted == null || u.Isdeleted == 0) && !string.IsNullOrWhiteSpace(u.Email))
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(email)) return;
        var n = BuildNotification(notificationType, null, null, null, organization.Id, NotificationChannel.Email, email, subject, message);
        _db.Notifications.Add(n);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Создано уведомление типа {Type} для организации {OrganizationId}", notificationType, organization.Id);
    }

    public async Task TryCreateInsufficientBalanceAsync(OrganizationClient client, Invoice invoice, InvoicePayment payment, decimal requiredAmount, decimal currentBalance, CancellationToken ct = default)
    {
        var amountSom = requiredAmount / 100m;
        var balanceSom = currentBalance / 100m;
        var deficitSom = (requiredAmount - currentBalance) / 100m;
        var subject = "Недостаточно средств для автоплатежа";
        var message = $@"
<h2>Уважаемый(ая) {client.ClientName ?? "Клиент"}!</h2>
<p>Недостаточно средств на балансе для автоматической оплаты счёта <strong>{invoice.NameInvoice ?? "Счёт"}.</strong></p>
<p><strong>Требуется:</strong> {amountSom:N2} сом</p>
<p><strong>Текущий баланс:</strong> {balanceSom:N2} сом</p>
<p><strong>Недостаёт:</strong> {deficitSom:N2} сом</p>
<p><strong>Период:</strong> {payment.DateFrom:dd.MM.yyyy} - {payment.DateTo:dd.MM.yyyy}</p>
<p>Пожалуйста, пополните баланс. PayCode: <strong>{invoice.PayCode}</strong></p>";
        await TryCreatePaymentNotificationAsync(NotificationType.InsufficientBalance, client, invoice, payment, subject, message, ct);
    }

    public async Task TryCreatePaymentReminderAsync(string notificationType, int daysBefore, OrganizationClient client, Invoice invoice, InvoicePayment payment, decimal requiredAmount, CancellationToken ct = default)
    {
        var amountSom = requiredAmount / 100m;
        var subject = daysBefore == 1 ? "Напоминание: завтра срок оплаты" : $"Напоминание: до срока оплаты осталось {daysBefore} дня";
        var message = $@"
<h2>Уважаемый(ая) {client.ClientName ?? "Клиент"}!</h2>
<p>Напоминаем о предстоящей оплате по счёту <strong>{invoice.NameInvoice ?? "Счёт"}.</strong></p>
<p><strong>Сумма к оплате:</strong> {amountSom:N2} сом</p>
<p><strong>Период:</strong> {payment.DateFrom:dd.MM.yyyy} - {payment.DateTo:dd.MM.yyyy}</p>
<p>Срок оплаты через {daysBefore} дн. Пожалуйста, пополните баланс для автоматического списания. PayCode: <strong>{invoice.PayCode}</strong></p>";
        await TryCreatePaymentNotificationAsync(notificationType, client, invoice, payment, subject, message, ct);
    }

    public async Task TryCreateOverdueDayNotificationAsync(string notificationType, int dayNumber, OrganizationClient client, Invoice invoice, InvoicePayment payment, decimal requiredAmount, CancellationToken ct = default)
    {
        var amountSom = requiredAmount / 100m;
        var subject = $"Просроченный платёж ({dayNumber}-й день)";
        var message = $@"
<h2>Уважаемый(ая) {client.ClientName ?? "Клиент"}!</h2>
<p>У вас просрочен платёж по счёту <strong>{invoice.NameInvoice ?? "Счёт"}.</strong> Просрочка: {dayNumber} дн.</p>
<p><strong>Сумма к оплате:</strong> {amountSom:N2} сом</p>
<p><strong>Период:</strong> {payment.DateFrom:dd.MM.yyyy} - {payment.DateTo:dd.MM.yyyy}</p>
<p>Пожалуйста, пополните баланс или произведите оплату вручную. PayCode: <strong>{invoice.PayCode}</strong></p>";
        await TryCreatePaymentNotificationAsync(notificationType, client, invoice, payment, subject, message, ct);
    }

    public async Task TryCreateAutoPaymentSuccessAsync(OrganizationClient client, Invoice invoice, InvoicePayment payment, decimal paidAmount, CancellationToken ct = default)
    {
        var amountSom = paidAmount / 100m;
        var subject = "Автоматический платёж выполнен";
        var message = $@"
<h2>Уважаемый(ая) {client.ClientName ?? "Клиент"}!</h2>
<p>Автоматический платёж по счёту <strong>{invoice.NameInvoice ?? "Счёт"} успешно выполнен.</strong></p>
<p><strong>Сумма:</strong> {amountSom:N2} сом</p>
<p><strong>Период:</strong> {payment.DateFrom:dd.MM.yyyy} - {payment.DateTo:dd.MM.yyyy}</p>
<p>Спасибо за использование наших услуг!</p>";
        await TryCreatePaymentNotificationAsync(NotificationType.AutoPaymentSuccess, client, invoice, payment, subject, message, ct);
    }

    public async Task TryCreateSubscriptionExpiredAsync(Organization organization, CancellationToken ct = default)
    {
        var subject = "Подписка истекла";
        var message = $@"
<h2>Уважаемая организация {organization.Name ?? "—"}!</h2>
<p>Период подписки на платформу истёк. Доступ к сервису ограничен.</p>
<p>Продлите подписку для продолжения работы.</p>";
        await TryCreateSubscriptionNotificationAsync(NotificationType.SubscriptionExpired, organization, subject, message, ct);
    }

    public async Task TryCreateSubscriptionEndingIn3DaysAsync(Organization organization, DateOnly periodEnd, CancellationToken ct = default)
    {
        var subject = "Подписка заканчивается через 3 дня";
        var message = $@"
<h2>Уважаемая организация {organization.Name ?? "—"}!</h2>
<p>Напоминаем: период подписки заканчивается <strong>{periodEnd:dd.MM.yyyy}</strong> (через 3 дня).</p>
<p>Продлите подписку заранее, чтобы не прерывать работу.</p>";
        await TryCreateSubscriptionNotificationAsync(NotificationType.SubscriptionEndingIn3Days, organization, subject, message, ct);
    }

    public async Task TryCreateSubscriptionEndingSoonAsync(Organization organization, DateOnly periodEnd, CancellationToken ct = default)
    {
        var subject = "Подписка заканчивается завтра";
        var message = $@"
<h2>Уважаемая организация {organization.Name ?? "—"}!</h2>
<p>Напоминаем: период подписки заканчивается <strong>{periodEnd:dd.MM.yyyy}</strong>.</p>
<p>Продлите подписку заранее.</p>";
        await TryCreateSubscriptionNotificationAsync(NotificationType.SubscriptionEndingSoon, organization, subject, message, ct);
    }

    public async Task TryCreateSubscriptionReminderPaymentAsync(Organization organization, DateOnly periodEnd, CancellationToken ct = default)
    {
        var subject = "Напоминание: сегодня последний день подписки";
        var message = $@"
<h2>Уважаемая организация {organization.Name ?? "—"}!</h2>
<p>Сегодня, <strong>{periodEnd:dd.MM.yyyy}</strong>, заканчивается период подписки.</p>
<p>Оплатите подписку для продолжения доступа.</p>";
        await TryCreateSubscriptionNotificationAsync(NotificationType.SubscriptionReminderPayment, organization, subject, message, ct);
    }
}
