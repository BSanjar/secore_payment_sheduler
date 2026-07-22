using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using InvoiceSchedulerJob.Models.DBModels;
using InvoiceSchedulerJob.Helpers;
using static InvoiceSchedulerJob.Constants;

namespace InvoiceSchedulerJob.Services;

/// <summary>
/// Обработка платежей: день оплаты (автосписание или уведомление о недостатке средств) и просроченные по этапам (1, 3, 7 дней).
/// Сравнение по датам без времени.
/// </summary>
public class InvoicePaymentService
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notificationService;
    private readonly ILogger<InvoicePaymentService> _logger;

    public InvoicePaymentService(AppDbContext db, NotificationService notificationService, ILogger<InvoicePaymentService> logger)
    {
        _db = db;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task ProcessScheduledPaymentsAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Now.Date);
        var todayStart = today.ToDateTime(TimeOnly.MinValue);

        var dueOrOverdue = await _db.InvoicePayments
            .Include(ip => ip.InvoiceNavigation)
            .ThenInclude(i => i!.ClientNavigation)
            .Where(ip => ip.PaymentStatus == PaymentStatus.NonPaid
                        && ip.DateTo.HasValue
                        && ip.DateTo.Value.Date <= todayStart.Date
                        && ip.InvoiceNavigation != null
                        && ip.InvoiceNavigation.InvoiceStatus == InvoiceStatus.Actual
                        && ip.InvoiceNavigation.ClientNavigation != null)
            .ToListAsync(ct);

        var reminderPayments = await _db.InvoicePayments
            .Include(ip => ip.InvoiceNavigation)
            .ThenInclude(i => i!.ClientNavigation)
            .Where(ip => ip.PaymentStatus == PaymentStatus.NonPaid
                        && ip.DateTo.HasValue
                        && ip.DateTo.Value.Date > todayStart.Date
                        && ip.InvoiceNavigation != null
                        && ip.InvoiceNavigation.InvoiceStatus == InvoiceStatus.Actual
                        && ip.InvoiceNavigation.ClientNavigation != null)
            .ToListAsync(ct);

        var dueToday = dueOrOverdue.Where(p => p.DateTo!.Value.Date == todayStart.Date).ToList();
        var overdue = dueOrOverdue.Where(p => p.DateTo!.Value.Date < todayStart.Date).ToList();
        var reminder1 = reminderPayments.Where(p => p.DateTo!.Value.Date == todayStart.AddDays(1).Date).ToList();
        var reminder3 = reminderPayments.Where(p => p.DateTo!.Value.Date == todayStart.AddDays(3).Date).ToList();

        _logger.LogInformation("Платежи: день оплаты {DueToday}, просроченные {Overdue}, напоминания за 1 дн. {R1}, за 3 дн. {R3}", dueToday.Count, overdue.Count, reminder1.Count, reminder3.Count);

        foreach (var payment in reminder3)
        {
            try { await ProcessPaymentReminderAsync(payment, 3, ct); }
            catch (Exception ex) { _logger.LogError(ex, "Ошибка напоминания за 3 дня {PaymentId}", payment.Id); }
        }
        foreach (var payment in reminder1)
        {
            try { await ProcessPaymentReminderAsync(payment, 1, ct); }
            catch (Exception ex) { _logger.LogError(ex, "Ошибка напоминания за 1 день {PaymentId}", payment.Id); }
        }
        foreach (var payment in dueToday)
        {
            try { await ProcessDueTodayAsync(payment, today, ct); }
            catch (Exception ex) { _logger.LogError(ex, "Ошибка обработки платежа (день оплаты) {PaymentId}", payment.Id); }
        }
        foreach (var payment in overdue)
        {
            try { await ProcessOverdueAsync(payment, today, ct); }
            catch (Exception ex) { _logger.LogError(ex, "Ошибка обработки просроченного платежа {PaymentId}", payment.Id); }
        }
    }

    /// <summary>
    /// Напоминание об оплате за 1 или 3 дня до срока (без дубля).
    /// </summary>
    private async Task ProcessPaymentReminderAsync(InvoicePayment payment, int daysBefore, CancellationToken ct)
    {
        var invoice = payment.InvoiceNavigation!;
        var client = invoice.ClientNavigation!;
        var requiredAmount = ResolveRequiredAmount(invoice, payment);
        if (requiredAmount == null) return;
        var notificationType = daysBefore == 1 ? NotificationType.PaymentReminder1Day : NotificationType.PaymentReminder3Days;
        await _notificationService.TryCreatePaymentReminderAsync(notificationType, daysBefore, client, invoice, payment, requiredAmount.Value, ct);
    }

    /// <summary>
    /// День оплаты: попытка автосписания; при недостатке средств — уведомление InsufficientBalance (без дубля).
    /// </summary>
    private async Task ProcessDueTodayAsync(InvoicePayment payment, DateOnly today, CancellationToken ct)
    {
        var invoice = payment.InvoiceNavigation!;
        var client = invoice.ClientNavigation!;
        var requiredAmount = ResolveRequiredAmount(invoice, payment);
        if (requiredAmount == null)
        {
            _logger.LogWarning("Не удалось определить сумму платежа {PaymentId}", payment.Id);
            return;
        }
        var amount = requiredAmount.Value;
        var invoiceBalance = invoice.Balance ?? 0m;

        if (invoiceBalance >= amount)
        {
            await ExecuteAutoPaymentAsync(payment, invoice, client, amount, ct);
            return;
        }

        await _notificationService.TryCreateInsufficientBalanceAsync(client, invoice, payment, amount, invoiceBalance, ct);
        _logger.LogInformation(
            "Недостаточно средств: PaymentId={PaymentId}, InvoiceId={InvoiceId}, ClientId={ClientId}, Required={Required}, Balance={Balance}",
            payment.Id, invoice.Id, client.Id, amount, invoiceBalance);
    }

    /// <summary>
    /// Просроченный платёж: уведомление только в заданные дни (1, 3, 7). Без повторной проверки «недостаточно средств».
    /// </summary>
    private async Task ProcessOverdueAsync(InvoicePayment payment, DateOnly today, CancellationToken ct)
    {
        var invoice = payment.InvoiceNavigation!;
        var client = invoice.ClientNavigation!;
        var dueDate = DateOnly.FromDateTime(payment.DateTo!.Value.Date);
        var daysOverdue = (today.DayNumber - dueDate.DayNumber);
        if (daysOverdue <= 0) return;

        if (!OverdueNotificationDays.Contains(daysOverdue))
            return;

        var requiredAmount = ResolveRequiredAmount(invoice, payment);
        if (requiredAmount == null) return;
        var amount = requiredAmount.Value;

        var notificationType = daysOverdue switch
        {
            1 => NotificationType.OverdueDay1,
            3 => NotificationType.OverdueDay3,
            7 => NotificationType.OverdueDay7,
            _ => (string?)null
        };
        if (string.IsNullOrEmpty(notificationType)) return;

        await _notificationService.TryCreateOverdueDayNotificationAsync(notificationType, daysOverdue, client, invoice, payment, amount, ct);
    }

    private static decimal? ResolveRequiredAmount(Invoice invoice, InvoicePayment payment)
    {
        if (invoice.FixedSumm is { } v && v > 0) return decimal.Round(v, 0, MidpointRounding.AwayFromZero);
        if (invoice.Balance is { } b && b < 0) return decimal.Round(Math.Abs(b), 0, MidpointRounding.AwayFromZero);
        if (payment.PaymentSumm is { } p && p > 0) return decimal.Round(p, 0, MidpointRounding.AwayFromZero);
        return null;
    }

    private async Task ExecuteAutoPaymentAsync(InvoicePayment payment, Invoice invoice, OrganizationClient client, decimal amount, CancellationToken ct)
    {
        var isOneTime = string.Equals(invoice.Periodicity, InvoiceStatus.PeriodicityOneTime, StringComparison.OrdinalIgnoreCase);
        var now = ParsersHelper.NowForTimestamp();

        using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            if (!isOneTime)
            {
                _db.Transactions.Add(new Transaction
                {
                    Id = Guid.NewGuid().ToString(),
                    TransactionDate = now,
                    TransactionStatus = TransactionType.StatusSuccess,
                    Summ = amount,
                    TransactionSumm = amount,
                    Invoice = invoice.Id,
                    TransactionType = TransactionType.Credit
                });
            }

            invoice.Balance = (invoice.Balance ?? 0m) - amount;
            payment.PaymentStatus = PaymentStatus.Paid;
            if (isOneTime) invoice.InvoiceStatus = InvoiceStatus.Closed;

            if (!isOneTime && invoice.AutoProlongation == true && payment.DateTo.HasValue)
                await CreateNextPaymentPeriodAsync(invoice, payment.DateTo.Value);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            _logger.LogInformation(
                "Автоплатёж выполнен: PaymentId={PaymentId}, InvoiceId={InvoiceId}, ClientId={ClientId}, OrgId={OrgId}, Amount={Amount}",
                payment.Id, invoice.Id, client.Id, client.Organization, amount);

            try
            {
                await _notificationService.TryCreateAutoPaymentSuccessAsync(client, invoice, payment, amount, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка создания уведомления об успешном платеже {PaymentId}", payment.Id);
            }
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            _logger.LogError(ex, "Ошибка автоплатежа PaymentId={PaymentId}, InvoiceId={InvoiceId}, ClientId={ClientId}",
                payment.Id, invoice.Id, client.Id);
            throw;
        }
    }

    private Task CreateNextPaymentPeriodAsync(Invoice invoice, DateTime previousPeriodEnd)
    {
        if (string.IsNullOrWhiteSpace(invoice.Periodicity)
            || string.Equals(invoice.Periodicity, InvoiceStatus.PeriodicityOneTime, StringComparison.OrdinalIgnoreCase)
            || string.Equals(invoice.Periodicity, InvoiceStatus.PeriodicityAny, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        var nextDateFrom = previousPeriodEnd.Date.AddDays(1);
        DateTime nextDateTo;
        switch (invoice.Periodicity.ToLower())
        {
            case "daily":
                nextDateTo = nextDateFrom;
                break;
            case "weekly":
                nextDateTo = nextDateFrom.AddDays(6);
                break;
            case "monthly":
                nextDateTo = nextDateFrom.AddMonths(1).AddDays(-1);
                break;
            case "yearly":
                nextDateTo = nextDateFrom.AddYears(1).AddDays(-1);
                break;
            default:
                if (int.TryParse(invoice.Periodicity, out var days))
                    nextDateTo = nextDateFrom.AddDays(days - 1);
                else
                    return Task.CompletedTask;
                break;
        }

        _db.InvoicePayments.Add(new InvoicePayment
        {
            Id = Guid.NewGuid().ToString(),
            Invoice = invoice.Id,
            DateFrom = nextDateFrom,
            DateTo = nextDateTo,
            PaymentStatus = PaymentStatus.NonPaid,
            PaymentSumm = invoice.FixedSumm
        });
        invoice.NextStartInvoice = nextDateFrom;
        _logger.LogInformation("Создан следующий период платежа для инвойса {InvoiceId}: {DateFrom} - {DateTo}", invoice.Id, nextDateFrom, nextDateTo);
        return Task.CompletedTask;
    }
}
