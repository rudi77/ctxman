using Microsoft.Extensions.Configuration;

namespace Ctxman.Api.Gc;

/// <summary>
/// Konfigurations-POCO für die Blob-Retention-Policy (Spec §7.1).
/// Konfigurations-Sektion: <c>retention</c>. Schlüssel in snake_case via
/// <see cref="ConfigurationKeyNameAttribute"/> (gleiches Muster wie <c>BlobStoreOptions</c>).
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "retention";

    /// <summary>
    /// Forensik-/Race-Fenster in Stunden vor dem Löschen (Spec §7.1: <c>blob_grace_hours</c>, Default 72h).
    /// </summary>
    [ConfigurationKeyName("blob_grace_hours")]
    public int BlobGraceHours { get; set; } = 72;

    /// <summary>
    /// Haltezeit für Blobs eviktierter (nicht-live) Segmente aktiver Sessions in Tagen
    /// (Spec §7.1: <c>evicted_blob_retention_days</c>, Default 0 = kein zusätzliches Fenster).
    /// </summary>
    [ConfigurationKeyName("evicted_blob_retention_days")]
    public int EvictedBlobRetentionDays { get; set; } = 0;

    /// <summary>
    /// Verhalten bei Blobs archivierter Sessions (Spec §7.1: <c>archived_session_blobs</c>, Default "delete").
    /// </summary>
    [ConfigurationKeyName("archived_session_blobs")]
    public string ArchivedSessionBlobs { get; set; } = "delete";

    /// <summary>
    /// Sweep-Intervall im Config-Format (z. B. "24h") (Spec §7.1: <c>sweep_interval</c>).
    /// </summary>
    [ConfigurationKeyName("sweep_interval")]
    public string SweepInterval { get; set; } = "24h";

    /// <summary>Konvertiert in das Core-Domain-Record <see cref="Ctxman.Core.Domain.RetentionConfig"/>.</summary>
    public Ctxman.Core.Domain.RetentionConfig ToRetentionConfig() =>
        new(BlobGraceHours, EvictedBlobRetentionDays, ArchivedSessionBlobs, SweepInterval);
}
