using System;
using System.Collections.Generic;

namespace InvoiceSchedulerJob.Models.DBModels;

public partial class Notification
{
    public string Id { get; set; } = null!;

    /// <summary>
    /// Тип события: insufficient_balance, payment_reminder_1_day, payment_reminder_3_days, overdue_day_1/3/7, auto_payment_success, subscription_ending_in_3_days, subscription_ending_soon, subscription_reminder_payment, subscription_expired.
    /// </summary>
    public string? NotificationType { get; set; }

    public string? ClientId { get; set; }

    public string? InvoiceId { get; set; }

    public string? InvoicePaymentId { get; set; }

    public string? OrganizationId { get; set; }

    public string? Channel { get; set; }

    public string? ContactInfo { get; set; }

    public string? Subject { get; set; }

    public string? Message { get; set; }

    public string? Status { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public DateTime? SentAt { get; set; }

    public int? RetryCount { get; set; }

    public string? ErrorMessage { get; set; }

    public string? Metadata { get; set; }

    public virtual OrganizationClient? Client { get; set; }
}
