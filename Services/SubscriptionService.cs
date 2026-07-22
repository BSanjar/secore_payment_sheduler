using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using InvoiceSchedulerJob.Models.DBModels;
using static InvoiceSchedulerJob.Constants;

namespace InvoiceSchedulerJob.Services;

/// <summary>
/// Проверка подписки организаций и уведомления об окончании/истечении подписки.
/// </summary>
public class SubscriptionService
    {
        private readonly AppDbContext _db;
        private readonly NotificationService _notificationService;
        private readonly ILogger<SubscriptionService> _logger;

        public SubscriptionService(
            AppDbContext db,
            NotificationService notificationService,
            ILogger<SubscriptionService> logger)
        {
            _db = db;
            _notificationService = notificationService;
            _logger = logger;
        }

        /// <summary>
        /// Есть ли у организации доступ по подписке на указанную дату; при отсутствии оплаты организация деактивируется.
        /// </summary>
        public static async Task<bool> HasSubscriptionAccessAsync(
            AppDbContext db,
            string organizationId,
            DateTime? date = null,
            CancellationToken ct = default)
        {
            var checkDate = date != null ? DateOnly.FromDateTime(date.Value.Date) : DateOnly.FromDateTime(DateTime.UtcNow.Date);
            var org = await db.Organizations
                .Include(o => o.OrganizationSetting)
                .FirstOrDefaultAsync(o => o.Id == organizationId, ct);
            if (org == null)
                return false;

            var billingType = org.OrganizationSetting?.BillingType;
            if (!string.Equals(billingType, BillingType.Subscription, StringComparison.OrdinalIgnoreCase))
                return true;

            var hasPayment = await db.OrganizationSubscriptionPayments
                .AnyAsync(p => p.OrganizationId == organizationId
                    && p.PeriodStart <= checkDate
                    && p.PeriodEnd >= checkDate, ct);

            if (!hasPayment)
            {
                if (org.IsActive == true)
                {
                    org.IsActive = false;
                    await db.SaveChangesAsync(ct);
                }
                return false;
            }

            if (org.IsActive == false)
            {
                org.IsActive = true;
                await db.SaveChangesAsync(ct);
            }
            return true;
        }

        /// <summary>
        /// Дата окончания текущего оплаченного периода (включительно) или null.
        /// </summary>
        public static async Task<DateOnly?> GetCurrentPeriodEndAsync(
            AppDbContext db,
            string organizationId,
            DateTime? date = null,
            CancellationToken ct = default)
        {
            var checkDate = date != null ? DateOnly.FromDateTime(date.Value.Date) : DateOnly.FromDateTime(DateTime.UtcNow.Date);
            var payment = await db.OrganizationSubscriptionPayments
                .Where(p => p.OrganizationId == organizationId && p.PeriodStart <= checkDate && p.PeriodEnd >= checkDate)
                .OrderByDescending(p => p.PeriodEnd)
                .Select(p => new { p.PeriodEnd })
                .FirstOrDefaultAsync(ct);
            return payment?.PeriodEnd;
        }

        /// <summary>
        /// Обрабатывает все организации с биллингом subscription: проверка доступа, деактивация при истечении, уведомления (за 1 день до конца, в день окончания, после истечения).
        /// </summary>
        public async Task ProcessSubscriptionChecksAsync(CancellationToken ct = default)
        {
            var checkDate = DateOnly.FromDateTime(DateTime.Now.Date);

            var orgsWithSubscription = await _db.Organizations
                .Include(o => o.OrganizationSetting)
                .Where(o => o.OrganizationSetting != null
                    && o.OrganizationSetting.BillingType != null
                    && o.OrganizationSetting.BillingType.ToLower() == BillingType.Subscription)
                .ToListAsync(ct);

            _logger.LogInformation("Проверка подписок: найдено организаций с типом subscription: {Count}", orgsWithSubscription.Count);

            foreach (var org in orgsWithSubscription)
            {
                try
                {
                    await ProcessOrganizationSubscriptionAsync(org, checkDate, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при проверке подписки OrgId={OrganizationId}", org.Id);
                }
            }
        }

        private async Task ProcessOrganizationSubscriptionAsync(
            Organization org,
            DateOnly checkDate,
            CancellationToken ct)
        {
            var payment = await _db.OrganizationSubscriptionPayments
                .Where(p => p.OrganizationId == org.Id && p.PeriodStart <= checkDate && p.PeriodEnd >= checkDate)
                .OrderByDescending(p => p.PeriodEnd)
                .FirstOrDefaultAsync(ct);

            if (payment == null)
            {
                if (org.IsActive == true)
                {
                    org.IsActive = false;
                    await _db.SaveChangesAsync(ct);
                    _logger.LogInformation(
                        "Организация OrgId={OrganizationId} ({Name}) деактивирована: подписка истекла",
                        org.Id, org.Name);
                }
                await _notificationService.TryCreateSubscriptionExpiredAsync(org, ct);
                return;
            }

            if (org.IsActive == false)
            {
                org.IsActive = true;
                await _db.SaveChangesAsync(ct);
            }

            var periodEnd = payment.PeriodEnd;

            if (periodEnd == checkDate)
                await _notificationService.TryCreateSubscriptionReminderPaymentAsync(org, periodEnd, ct);
            else if (periodEnd == checkDate.AddDays(1))
                await _notificationService.TryCreateSubscriptionEndingSoonAsync(org, periodEnd, ct);
            else if (periodEnd == checkDate.AddDays(3))
                await _notificationService.TryCreateSubscriptionEndingIn3DaysAsync(org, periodEnd, ct);
        }
}
