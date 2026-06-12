namespace Ctxman.Core.Domain;

/// <summary>
/// Watermark-Schwellen relativ zum Modell-Context-Budget B (Spec §3.1 / §5).
/// Defaults: soft 0.60·B, hard 0.80·B, emergency 0.95·B.
/// </summary>
public sealed record Watermarks(
    double Soft,
    double Hard,
    double Emergency);

/// <summary>
/// Pro-Kind-Policy (Spec §5). <see cref="TtlTurns"/> == null bildet unendliche TTL ab
/// (in der Spec als ∞ notiert, z. B. für decision/task).
/// </summary>
public sealed record KindPolicy(
    int? TtlTurns = null,
    bool Externalize = false,
    bool Refetchable = false,
    bool Promote = false);

/// <summary>
/// Konfiguration der Major-Collection-Compaction (Spec §3.3 / §5).
/// </summary>
public sealed record CompactionConfig(
    string Model,
    string PromptTemplateId,
    double MaxShare);

/// <summary>
/// Senke für die Fact-Promotion (Spec §3.3 / §5).
/// </summary>
public sealed record PromotionSink(
    string Type,
    string? Url = null);

/// <summary>
/// Promotion-Konfiguration (Spec §5).
/// </summary>
public sealed record PromotionConfig(
    PromotionSink Sink);

/// <summary>
/// Blob-Retention/Mark-and-Sweep-Konfiguration (Spec §7.1). <see cref="BlobGraceHours"/> und
/// <see cref="SweepInterval"/> sind das Wire-/Config-Format; <see cref="BlobGrace"/> und
/// <see cref="SweepIntervalSpan"/> liefern die vom Sweeper konsumierten <see cref="TimeSpan"/>-Werte.
/// </summary>
public sealed record RetentionConfig(
    int BlobGraceHours,
    int EvictedBlobRetentionDays,
    string ArchivedSessionBlobs,
    string SweepInterval)
{
    /// <summary>
    /// Mindest-Alter vor Sweep als <see cref="TimeSpan"/> (Spec §7.1: <c>blob_grace_hours</c>).
    /// </summary>
    public TimeSpan BlobGrace => TimeSpan.FromHours(BlobGraceHours);

    /// <summary>
    /// Sweep-Intervall als <see cref="TimeSpan"/> (Spec §7.1: <c>sweep_interval</c>, z. B. "24h").
    /// </summary>
    public TimeSpan SweepIntervalSpan => ParseDuration(SweepInterval);

    /// <summary>
    /// Parst eine Dauer aus dem Config-Format: entweder mit Suffix (<c>h</c> Stunden,
    /// <c>m</c> Minuten, <c>s</c> Sekunden, <c>d</c> Tage) wie "24h", oder eine reine Zahl,
    /// die als Stunden interpretiert wird (Spec §7.1).
    /// </summary>
    public static TimeSpan ParseDuration(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();

        var unit = trimmed[^1];
        if (char.IsLetter(unit))
        {
            var number = trimmed[..^1];
            if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var magnitude))
            {
                throw new FormatException($"Ungültige Dauer '{value}'.");
            }

            return char.ToLowerInvariant(unit) switch
            {
                'h' => TimeSpan.FromHours(magnitude),
                'm' => TimeSpan.FromMinutes(magnitude),
                's' => TimeSpan.FromSeconds(magnitude),
                'd' => TimeSpan.FromDays(magnitude),
                _ => throw new FormatException($"Unbekannte Zeiteinheit '{unit}' in '{value}'."),
            };
        }

        if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var hours))
        {
            throw new FormatException($"Ungültige Dauer '{value}'.");
        }

        return TimeSpan.FromHours(hours);
    }
}

/// <summary>
/// Effektive, deklarative Policy einer Session (Spec §5 + §7.1). Wird bei Session-Erstellung
/// als Snapshot eingefroren (Reproduzierbarkeit). Property-Namen sind PascalCase;
/// die snake_case-Wire-Abbildung ist Serialisierungs-Belang (späterer Subtask).
/// </summary>
public sealed record PolicyConfig(
    int BudgetTokens,
    Watermarks Watermarks,
    int ExternalizeThresholdTokens,
    string Tokenizer,
    IReadOnlyDictionary<string, KindPolicy> Kinds,
    CompactionConfig Compaction,
    PromotionConfig Promotion,
    RetentionConfig Retention,
    string OnToolRemoved = "externalize")
{
    /// <summary>
    /// Spec-Defaults aus §5 (Beispiel-Policy) und §3.1 (Watermarks) / §7.1 (retention).
    /// Unendliche TTL (∞) ist als null in <see cref="KindPolicy.TtlTurns"/> abgebildet.
    /// </summary>
    public static PolicyConfig Default() => new(
        BudgetTokens: 180_000,
        Watermarks: new Watermarks(Soft: 0.60, Hard: 0.80, Emergency: 0.95),
        ExternalizeThresholdTokens: 2000,
        Tokenizer: "claude",
        Kinds: new Dictionary<string, KindPolicy>
        {
            ["tool_result"]   = new KindPolicy(TtlTurns: 2,  Externalize: true),
            ["ref_expansion"] = new KindPolicy(TtlTurns: 1,  Externalize: true),
            ["skill_content"] = new KindPolicy(TtlTurns: 8,  Refetchable: true),
            ["mcp_resource"]  = new KindPolicy(TtlTurns: 3,  Refetchable: true),
            ["user_msg"]      = new KindPolicy(TtlTurns: 40, Externalize: false),
            ["assistant_msg"] = new KindPolicy(TtlTurns: 40, Externalize: false),
            ["decision"]      = new KindPolicy(TtlTurns: null, Promote: true), // ∞
            ["task"]          = new KindPolicy(TtlTurns: null),                // ∞
        },
        Compaction: new CompactionConfig(
            Model: "claude-haiku-4-5",
            PromptTemplateId: "default-v1",
            MaxShare: 0.5),
        Promotion: new PromotionConfig(
            Sink: new PromotionSink(Type: "webhook", Url: "https://…/memory/ingest")),
        Retention: new RetentionConfig(
            BlobGraceHours: 72,
            EvictedBlobRetentionDays: 0,
            ArchivedSessionBlobs: "delete",
            SweepInterval: "24h"),
        // Spec §4.2: keep | externalize (Default) | evict — angewandt auf Units entfernter Tools.
        OnToolRemoved: "externalize");
}
