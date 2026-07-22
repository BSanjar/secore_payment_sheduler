namespace InvoiceSchedulerJob.Logging;

/// <summary>
/// Persistent file logging outside the container.
/// Server layout:
///   /secore/mainSystem   — web app
///   /secore/notificator  — notificator service
///   /secore/sheduler     — this payment scheduler service
/// </summary>
public sealed class SecoreLoggingOptions
{
    public const string SectionName = "SecoreLogging";

    /// <summary>Host folder mounted into the container. Default: /secore/sheduler</summary>
    public string RootPath { get; set; } = "/secore/sheduler";

    /// <summary>File name prefix. Daily files: {FilePrefix}-20260722.log</summary>
    public string FilePrefix { get; set; } = "sheduler";

    /// <summary>How many daily files to keep (null = unlimited).</summary>
    public int? RetainedFileCountLimit { get; set; } = 120;

    /// <summary>Minimum level written to file.</summary>
    public string MinimumLevel { get; set; } = "Information";
}
