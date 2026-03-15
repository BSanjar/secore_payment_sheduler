using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using InvoiceSchedulerJob.Services;

namespace InvoiceSchedulerJob;

/// <summary>
/// Фоновый сервис: ежедневный запуск платежей и проверки подписок в свои назначенные время.
/// </summary>
public class InvoiceSchedulerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<InvoiceSchedulerService> _logger;
    private readonly int _paymentHour;
    private readonly int _paymentMinute;
    private readonly int _subscriptionHour;
    private readonly int _subscriptionMinute;

    public InvoiceSchedulerService(
        IServiceProvider serviceProvider,
        ILogger<InvoiceSchedulerService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _paymentHour = Math.Clamp(configuration.GetValue("SchedulerSettings:RunTime:Hour", 2), 0, 23);
        _paymentMinute = Math.Clamp(configuration.GetValue("SchedulerSettings:RunTime:Minute", 0), 0, 59);
        _subscriptionHour = Math.Clamp(configuration.GetValue("SchedulerSettings:SubscriptionRunTime:Hour", 4), 0, 23);
        _subscriptionMinute = Math.Clamp(configuration.GetValue("SchedulerSettings:SubscriptionRunTime:Minute", 0), 0, 59);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Планировщик запущен. Платежи: {Ph:00}:{Pm:00}, подписки: {Sh:00}:{Sm:00}",
            _paymentHour, _paymentMinute, _subscriptionHour, _subscriptionMinute);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var nextPayment = GetNextRunTime(now, _paymentHour, _paymentMinute);
                var nextSubscription = GetNextRunTime(now, _subscriptionHour, _subscriptionMinute);
                var nextRun = nextPayment <= nextSubscription ? nextPayment : nextSubscription;
                var runPayments = nextRun == nextPayment;
                var runSubscriptions = nextRun == nextSubscription;

                var delay = nextRun - now;
                if (delay.TotalMilliseconds > 0)
                {
                    _logger.LogInformation("Следующий запуск: {Time} (через {Delay}) — {Tasks}",
                        nextRun.ToString("yyyy-MM-dd HH:mm:ss"), FormatDelay(delay),
                        runPayments && runSubscriptions ? "платежи и подписки" : runPayments ? "платежи" : "подписки");
                    await Task.Delay(delay, stoppingToken);
                }
                else
                {
                    _logger.LogWarning("Время запуска уже прошло, выполняем немедленно");
                }

                if (runPayments)
                    await RunPaymentsAsync(stoppingToken);
                if (runSubscriptions)
                    await RunSubscriptionsAsync(stoppingToken);
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Планировщик остановлен");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в цикле планировщика");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }

    private async Task RunPaymentsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var paymentService = scope.ServiceProvider.GetRequiredService<InvoicePaymentService>();
        _logger.LogInformation("Начало обработки платежей: {Time}", DateTime.Now);
        await paymentService.ProcessScheduledPaymentsAsync(ct);
        _logger.LogInformation("Обработка платежей завершена: {Time}", DateTime.Now);
    }

    private async Task RunSubscriptionsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
        _logger.LogInformation("Начало проверки подписок: {Time}", DateTime.Now);
        await subscriptionService.ProcessSubscriptionChecksAsync(ct);
        _logger.LogInformation("Проверка подписок завершена: {Time}", DateTime.Now);
    }

    private static DateTime GetNextRunTime(DateTime now, int hour, int minute)
    {
        var today = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
        return today <= now ? today.AddDays(1) : today;
    }

    private static string FormatDelay(TimeSpan d)
    {
        if (d.TotalDays >= 1) return $"{d.Days} дн. {d.Hours} ч. {d.Minutes} мин.";
        if (d.TotalHours >= 1) return $"{d.Hours} ч. {d.Minutes} мин.";
        return $"{d.Minutes} мин. {d.Seconds} сек.";
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Планировщик останавливается");
        await base.StopAsync(cancellationToken);
    }
}
