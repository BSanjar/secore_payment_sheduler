using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using InvoiceSchedulerJob.Models.DBModels;
using InvoiceSchedulerJob.Helpers;

namespace InvoiceSchedulerJob.Services
{
    /// <summary>
    /// Сервис для обработки автоматических платежей по инвойсам
    /// </summary>
    public class InvoicePaymentService
    {
        private readonly AppDbContext _db;
        private readonly NotificationService _notificationService;
        private readonly ILogger<InvoicePaymentService> _logger;

        public InvoicePaymentService(
            AppDbContext db,
            NotificationService notificationService,
            ILogger<InvoicePaymentService> logger)
        {
            _db = db;
            _notificationService = notificationService;
            _logger = logger;
        }

        /// <summary>
        /// Обрабатывает все просроченные и подошедшие платежи
        /// </summary>
        public async Task ProcessScheduledPaymentsAsync()
        {
            var now = ParsersHelper.NowForTimestamp();

            var duePayments = await _db.InvoicePayments
                .Include(ip => ip.InvoiceNavigation)
                    .ThenInclude(i => i!.ClientNavigation)
                .Where(ip => ip.PaymentStatus == "non_paid" && 
                            ip.DateTo.HasValue && 
                            ip.DateTo.Value <= now &&
                            ip.InvoiceNavigation != null &&
                            ip.InvoiceNavigation.InvoiceStatus == "actual" &&
                            ip.InvoiceNavigation.ClientNavigation != null)
                .ToListAsync();

            _logger.LogInformation("Найдено {Count} платежей для обработки", duePayments.Count);

            foreach (var payment in duePayments)
            {
                try
                {
                    await ProcessPaymentAsync(payment);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при обработке платежа {PaymentId}", payment.Id);
                }
            }
        }

        /// <summary>
        /// Обрабатывает один платёж
        /// </summary>
        private async Task ProcessPaymentAsync(InvoicePayment payment)
        {
            var invoice = payment.InvoiceNavigation!;
            var client = invoice.ClientNavigation!;

            decimal requiredAmount;
            if (invoice.FixedSumm.HasValue && invoice.FixedSumm.Value > 0)
            {
                requiredAmount = decimal.Round(invoice.FixedSumm.Value, 0, MidpointRounding.AwayFromZero);
            }
            else if (invoice.Balance.HasValue && invoice.Balance.Value < 0)
            {
                requiredAmount = decimal.Round(Math.Abs(invoice.Balance.Value), 0, MidpointRounding.AwayFromZero);
            }
            else if (payment.PaymentSumm.HasValue && payment.PaymentSumm.Value > 0)
            {
                requiredAmount = decimal.Round(payment.PaymentSumm.Value, 0, MidpointRounding.AwayFromZero);
            }
            else
            {
                _logger.LogWarning("Не удалось определить сумму платежа для {PaymentId}", payment.Id);
                return;
            }

            var invoiceBalance = invoice.Balance ?? 0m;
            var now = ParsersHelper.NowForTimestamp();
            var isOverdue = payment.DateTo.HasValue && payment.DateTo.Value < now;

            if (invoiceBalance >= requiredAmount)
            {
                await ExecuteAutoPaymentAsync(payment, invoice, client, requiredAmount);
            }
            else
            {
                if (isOverdue)
                {
                    await _notificationService.CreateOverduePaymentNotificationAsync(
                        client, invoice, payment, requiredAmount);
                }
                else
                {
                    await _notificationService.CreateInsufficientBalanceNotificationAsync(
                        client, invoice, payment, requiredAmount, invoiceBalance);
                }
            }
        }

        /// <summary>
        /// Выполняет автоматический платёж.
        /// Транзакция: не создаём для OneTime; создаём для всех остальных.
        /// Новая запись в invoice_payments: только если НЕ OneTime и стоит автопролонгация.
        /// Статус инвойса меняем только для OneTime (закрываем после оплаты).
        /// </summary>
        private async Task ExecuteAutoPaymentAsync(
            InvoicePayment payment, 
            Invoice invoice, 
            OrganizationClient client, 
            decimal amount)
        {
            var isOneTime = string.Equals(invoice.Periodicity, "oneTime", StringComparison.OrdinalIgnoreCase);

            using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                if (!isOneTime)
                {
                    var creditTransaction = new Transaction
                    {
                        Id = Guid.NewGuid().ToString(),
                        TransactionDate = ParsersHelper.NowForTimestamp(),
                        TransactionStatus = "success",
                        Summ = amount,
                        TransactionSumm = amount,
                        Invoice = invoice.Id,
                        TransactionType = "credit"
                    };
                    _db.Transactions.Add(creditTransaction);
                }

                invoice.Balance = (invoice.Balance ?? 0m) - amount;
                payment.PaymentStatus = "paid";

                if (isOneTime)
                {
                    invoice.InvoiceStatus = "closed";
                }

                if (!isOneTime && invoice.AutoProlongation == true && payment.DateTo.HasValue)
                {
                    await CreateNextPaymentPeriodAsync(invoice, payment.DateTo.Value);
                }

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation(
                    "Автоплатёж выполнен: PaymentId={PaymentId}, InvoiceId={InvoiceId}, Amount={Amount}", 
                    payment.Id, invoice.Id, amount);

                try
                {
                    await _notificationService.CreateAutoPaymentSuccessNotificationAsync(
                        client, invoice, payment, amount);
                }
                catch (Exception notifyEx)
                {
                    _logger.LogWarning(notifyEx, 
                        "Ошибка при создании уведомления об успешном платеже {PaymentId}. Платеж выполнен.", 
                        payment.Id);
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Ошибка при выполнении автоплатежа {PaymentId}", payment.Id);
                throw;
            }
        }

        /// <summary>
        /// Создаёт следующий период платежа при автопролонгации
        /// </summary>
        private Task CreateNextPaymentPeriodAsync(Invoice invoice, DateTime previousPeriodEnd)
        {
            if (string.IsNullOrWhiteSpace(invoice.Periodicity) || 
                invoice.Periodicity == "oneTime" || 
                invoice.Periodicity == "any")
            {
                return Task.CompletedTask;
            }

            var nextDateFrom = previousPeriodEnd.AddDays(1);
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
                    {
                        nextDateTo = nextDateFrom.AddDays(days - 1);
                    }
                    else
                    {
                        return Task.CompletedTask;
                    }
                    break;
            }

            var nextPayment = new InvoicePayment
            {
                Id = Guid.NewGuid().ToString(),
                Invoice = invoice.Id,
                DateFrom = nextDateFrom,
                DateTo = nextDateTo,
                PaymentStatus = "non_paid",
                PaymentSumm = invoice.FixedSumm
            };

            _db.InvoicePayments.Add(nextPayment);
            invoice.NextStartInvoice = nextDateFrom;

            _logger.LogInformation(
                "Создан следующий период платежа для инвойса {InvoiceId}: {DateFrom} - {DateTo}", 
                invoice.Id, nextDateFrom, nextDateTo);
            
            return Task.CompletedTask;
        }
    }
}

