using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using InvoiceSchedulerJob.Models.DBModels;
using InvoiceSchedulerJob.Services;

namespace InvoiceSchedulerJob
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            // Настройка конфигурации
            builder.Configuration
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
                .AddEnvironmentVariables();

            // Настройка логирования
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();

            // Регистрация базы данных
            var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("ConnectionString 'DefaultConnection' не найден в appsettings.json");
            }

            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(connectionString));

            // Регистрация сервисов
            builder.Services.AddScoped<NotificationService>();
            builder.Services.AddScoped<InvoicePaymentService>();

            var isTestRun = args.Contains("--test", StringComparer.OrdinalIgnoreCase) ||
                           args.Contains("--run-once", StringComparer.OrdinalIgnoreCase);

            if (!isTestRun)
            {
                builder.Services.AddHostedService<InvoiceSchedulerService>();
            }

            var host = builder.Build();

            var logger = host.Services.GetRequiredService<ILogger<Program>>();

            if (isTestRun)
            {
                logger.LogInformation("Режим теста: выполняется однократная обработка платежей...");
                using var scope = host.Services.CreateScope();
                var paymentService = scope.ServiceProvider.GetRequiredService<InvoicePaymentService>();
                await paymentService.ProcessScheduledPaymentsAsync();
                logger.LogInformation("Тестовый запуск завершён.");
                return;
            }

            logger.LogInformation("InvoiceSchedulerJob запущен");
            await host.RunAsync();
        }
    }

    /// <summary>
    /// Фоновый сервис, который запускает обработку платежей каждый день в определенное время
    /// </summary>
    public class InvoiceSchedulerService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<InvoiceSchedulerService> _logger;
        private readonly IConfiguration _configuration;
        private readonly int _runHour;
        private readonly int _runMinute;

        public InvoiceSchedulerService(
            IServiceProvider serviceProvider,
            ILogger<InvoiceSchedulerService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;

            // Читаем настройки времени запуска из конфигурации
            var runHour = _configuration.GetValue<int>("SchedulerSettings:RunTime:Hour", 2);
            var runMinute = _configuration.GetValue<int>("SchedulerSettings:RunTime:Minute", 0);

            // Валидация времени
            if (runHour < 0 || runHour > 23)
            {
                _logger.LogWarning("Некорректное значение часа ({Hour}), используется значение по умолчанию: 2", runHour);
                runHour = 2;
            }
            if (runMinute < 0 || runMinute > 59)
            {
                _logger.LogWarning("Некорректное значение минуты ({Minute}), используется значение по умолчанию: 0", runMinute);
                runMinute = 0;
            }

            _runHour = runHour;
            _runMinute = runMinute;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "InvoiceSchedulerService запущен. Обработка платежей будет выполняться каждый день в {Hour:00}:{Minute:00}",
                _runHour, _runMinute);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Вычисляем время следующего запуска
                    var nextRunTime = GetNextRunTime();
                    var delay = nextRunTime - DateTime.Now;

                    if (delay.TotalMilliseconds > 0)
                    {
                        _logger.LogInformation(
                            "Следующая обработка платежей запланирована на {NextRunTime} (через {Delay})",
                            nextRunTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            FormatDelay(delay));

                        // Ждем до времени запуска
                        await Task.Delay(delay, stoppingToken);
                    }
                    else
                    {
                        // Если время уже прошло (не должно происходить, но на всякий случай)
                        _logger.LogWarning("Время запуска уже прошло, запускаем немедленно");
                    }

                    // Выполняем обработку платежей
                    await ProcessPaymentsAsync(stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // Нормальная отмена при остановке сервиса
                    _logger.LogInformation("InvoiceSchedulerService получил сигнал остановки");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка в цикле выполнения InvoiceSchedulerService");
                    // В случае ошибки ждем 1 час перед повторной попыткой
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
            }
        }

        /// <summary>
        /// Вычисляет время следующего запуска (сегодня в указанное время, или завтра если время уже прошло)
        /// </summary>
        private DateTime GetNextRunTime()
        {
            var now = DateTime.Now;
            var todayRunTime = new DateTime(now.Year, now.Month, now.Day, _runHour, _runMinute, 0);

            // Если время сегодня уже прошло, планируем на завтра
            if (todayRunTime <= now)
            {
                return todayRunTime.AddDays(1);
            }

            return todayRunTime;
        }

        /// <summary>
        /// Форматирует задержку в читаемый формат
        /// </summary>
        private string FormatDelay(TimeSpan delay)
        {
            if (delay.TotalDays >= 1)
            {
                return $"{delay.Days} дн. {delay.Hours} ч. {delay.Minutes} мин.";
            }
            else if (delay.TotalHours >= 1)
            {
                return $"{delay.Hours} ч. {delay.Minutes} мин.";
            }
            else
            {
                return $"{delay.Minutes} мин. {delay.Seconds} сек.";
            }
        }

        private async Task ProcessPaymentsAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Начало обработки запланированных платежей: {Time}", DateTime.Now);

                using var scope = _serviceProvider.CreateScope();
                var paymentService = scope.ServiceProvider.GetRequiredService<InvoicePaymentService>();

                // Выполняем обработку платежей напрямую
                await paymentService.ProcessScheduledPaymentsAsync();

                _logger.LogInformation("Обработка запланированных платежей завершена успешно: {Time}", DateTime.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при обработке запланированных платежей");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("InvoiceSchedulerService останавливается");
            await base.StopAsync(cancellationToken);
        }
    }
}
