using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using InvoiceSchedulerJob.Models.DBModels;
using InvoiceSchedulerJob.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

var connectionString = builder.Configuration.GetSection("ConnectionStrings")["DefaultConnection"]
    ?? throw new InvalidOperationException("ConnectionString 'DefaultConnection' не найден.");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<InvoicePaymentService>();
builder.Services.AddScoped<SubscriptionService>();

var isTestRun = args.Contains("--test", StringComparer.OrdinalIgnoreCase)
    || args.Contains("--run-once", StringComparer.OrdinalIgnoreCase);

if (!isTestRun)
    builder.Services.AddHostedService<InvoiceSchedulerJob.InvoiceSchedulerService>();

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

if (isTestRun)
{
    logger.LogInformation("Режим теста: однократный запуск платежей и подписок...");
    using var scope = host.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<InvoicePaymentService>().ProcessScheduledPaymentsAsync();
    await scope.ServiceProvider.GetRequiredService<SubscriptionService>().ProcessSubscriptionChecksAsync();
    logger.LogInformation("Тест завершён.");
    return;
}

logger.LogInformation("InvoiceSchedulerJob запущен");
await host.RunAsync();
