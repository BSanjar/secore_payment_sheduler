using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using InvoiceSchedulerJob.Models.DBModels;
using InvoiceSchedulerJob.Services;
using InvoiceSchedulerJob.Logging;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

Log.Logger = SecoreLoggingBootstrap.Configure(
        new LoggerConfiguration(),
        builder.Configuration,
        builder.Environment)
    .CreateLogger();

builder.Services.AddSerilog();
builder.Services.Configure<SecoreLoggingOptions>(
    builder.Configuration.GetSection(SecoreLoggingOptions.SectionName));

var connectionString = builder.Configuration.GetSection("ConnectionStrings")["DefaultConnection"]
    ?? throw new InvalidOperationException("ConnectionString 'DefaultConnection' не найден.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<InvoicePaymentService>();
builder.Services.AddScoped<SubscriptionService>();

var isTestRun = args.Contains("--test", StringComparer.OrdinalIgnoreCase)
    || args.Contains("--run-once", StringComparer.OrdinalIgnoreCase);

if (!isTestRun)
    builder.Services.AddHostedService<InvoiceSchedulerJob.InvoiceSchedulerService>();

var logRoot = SecoreLoggingBootstrap.ResolveRootPath(builder.Configuration, builder.Environment);
Log.Information("SECORE sheduler starting. Environment={Environment}, Logs={LogRoot}",
    builder.Environment.EnvironmentName, logRoot);

try
{
    var host = builder.Build();

    if (isTestRun)
    {
        Log.Information("Режим теста: однократный запуск платежей и подписок...");
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InvoicePaymentService>().ProcessScheduledPaymentsAsync();
        await scope.ServiceProvider.GetRequiredService<SubscriptionService>().ProcessSubscriptionChecksAsync();
        Log.Information("Тест завершён.");
        return;
    }

    Log.Information("InvoiceSchedulerJob запущен");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "SECORE sheduler terminated unexpectedly");
    throw;
}
finally
{
    Log.Information("SECORE sheduler stopping.");
    Log.CloseAndFlush();
}
