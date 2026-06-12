using Prometheus;

namespace Ctxman.Api.Observability;

/// <summary>
/// Prometheus metric collectors for ctxman (Spec §6).
/// All collectors are created once at construction time; prometheus-net returns the same
/// instance if the name is already registered, so a DI singleton is correct.
/// </summary>
public sealed class CtxmanMetrics
{
    // Spec §6: render latency (p50/p99).
    public readonly Histogram RenderDuration = Metrics.CreateHistogram(
        "ctxman_render_duration_seconds",
        "Render latency in seconds (Spec §6).");

    // Spec §6: tokens per session at render time.
    public readonly Histogram RenderTokens = Metrics.CreateHistogram(
        "ctxman_render_tokens",
        "Tokens per session at render time (Spec §6).");

    // Spec §6: watermark distribution — counter with label 'state'.
    public readonly Counter Watermark = Metrics.CreateCounter(
        "ctxman_render_watermark_total",
        "Watermark distribution at render time (Spec §6).",
        new CounterConfiguration { LabelNames = new[] { "state" } });

    // Spec §6: compaction ratio (tokens_after / tokens_before).
    public readonly Histogram CompactionRatio = Metrics.CreateHistogram(
        "ctxman_compaction_ratio",
        "Compaction ratio (tokens_after / tokens_before) (Spec §6).");

    // Spec §6: page faults (ref expansions).
    public readonly Counter PageFaults = Metrics.CreateCounter(
        "ctxman_page_faults_total",
        "Page faults (ref expansions) (Spec §6).");

    // Spec §6: evictions of ref_expansion segments (mis-estimate indicator).
    public readonly Counter EvictionAfterExpansion = Metrics.CreateCounter(
        "ctxman_eviction_after_expansion_total",
        "Evictions of ref_expansion segments after page-fault expansion (Spec §6).");

    // Spec §6: static epoch bumps.
    public readonly Counter EpochBumps = Metrics.CreateCounter(
        "ctxman_epoch_bumps_total",
        "Static epoch bumps (Spec §6).");

    /// <summary>
    /// Observe a successful render: duration, tokens, and watermark state (Spec §6).
    /// </summary>
    public void ObserveRender(double seconds, int tokensTotal, string watermarkState)
    {
        RenderDuration.Observe(seconds);
        RenderTokens.Observe(tokensTotal);
        Watermark.WithLabels(watermarkState).Inc();
    }

    /// <summary>
    /// Observe a compaction: ratio = tokens_after / tokens_before (Spec §6).
    /// </summary>
    public void ObserveCompaction(int tokensBefore, int tokensAfter)
    {
        var ratio = tokensBefore > 0 ? (double)tokensAfter / tokensBefore : 0.0;
        CompactionRatio.Observe(ratio);
    }

    /// <summary>Increment the page-fault counter (Spec §6).</summary>
    public void RecordPageFault() => PageFaults.Inc();

    /// <summary>Increment the eviction-after-expansion counter (Spec §6).</summary>
    public void RecordEvictionAfterExpansion() => EvictionAfterExpansion.Inc();

    /// <summary>Increment the epoch-bump counter (Spec §6).</summary>
    public void RecordEpochBump() => EpochBumps.Inc();
}
