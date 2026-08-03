using System.Diagnostics.Metrics;

namespace Cs2Highlight.Web.Services;

public sealed class GenerationMetrics : IDisposable
{
    private readonly Meter meter = new("Cs2Highlight.Web", "1.0");
    public Counter<long> GenerationCreated { get; }
    public Counter<long> GenerationCompleted { get; }
    public Counter<long> GenerationFailed { get; }
    public Counter<long> CleanupDeletedBytes { get; }
    public Counter<long> CleanupFailures { get; }
    public Counter<long> TokensPurchased { get; }
    public Counter<long> TokensSpent { get; }
    public Counter<long> TokensRefunded { get; }
    public Counter<long> TokensReferral { get; }
    public Counter<long> PaymentsCompleted { get; }
    public Counter<long> PaymentsFailed { get; }
    public Histogram<double> GenerationDurationSeconds { get; }
    public Histogram<double> GenerationQueueDurationSeconds { get; }
    public Histogram<double> GenerationStageDurationSeconds { get; }

    public GenerationMetrics()
    {
        GenerationCreated = meter.CreateCounter<long>("generation_created_total");
        GenerationCompleted = meter.CreateCounter<long>("generation_completed_total");
        GenerationFailed = meter.CreateCounter<long>("generation_failed_total");
        CleanupDeletedBytes = meter.CreateCounter<long>("generation_cleanup_deleted_bytes");
        CleanupFailures = meter.CreateCounter<long>("generation_cleanup_failures_total");
        TokensPurchased = meter.CreateCounter<long>("tokens_purchased_total");
        TokensSpent = meter.CreateCounter<long>("tokens_spent_total");
        TokensRefunded = meter.CreateCounter<long>("tokens_refunded_total");
        TokensReferral = meter.CreateCounter<long>("tokens_referral_total");
        PaymentsCompleted = meter.CreateCounter<long>("payments_completed_total");
        PaymentsFailed = meter.CreateCounter<long>("payments_failed_total");
        GenerationDurationSeconds = meter.CreateHistogram<double>("generation_duration_seconds");
        GenerationQueueDurationSeconds = meter.CreateHistogram<double>("generation_queue_duration_seconds");
        GenerationStageDurationSeconds = meter.CreateHistogram<double>("generation_stage_duration_seconds");
    }

    public void Dispose() => meter.Dispose();
}
