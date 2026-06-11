namespace Ctxman.Core.Persistence;

/// <summary>
/// Eine Zeile der <c>idempotency_keys</c>-Tabelle (Spec §7 Tabellen / §4.4). Auf allen
/// mutierenden Endpunkten ist ein <c>Idempotency-Key</c> Pflicht; ein wiederholter Key liefert
/// dieselbe Antwort und löst keinen Doppel-Append aus (Spec §4.4). Aufbewahrung: 24 h —
/// hier als Datums-/Semantik-Feld (<see cref="ExpiresAt"/>) abgebildet; ein Cleanup-Job ist
/// nicht Teil dieses Subtasks.
///
/// Reines Persistenz-DTO. Die snake_case-Tabellen-/Spaltennamen und der zusammengesetzte
/// Schlüssel (tenant_id, key) setzt die Entity-Konfiguration (Subtask 6).
/// </summary>
public sealed class IdempotencyKeyRecord
{
    /// <summary>Der vom Client gesendete Idempotency-Key (Spec §4.4).</summary>
    public required string Key { get; init; }

    /// <summary>Tenant-Isolation auf Zeilenebene (Spec §7 / §10); kommt nie aus dem Body.</summary>
    public required string TenantId { get; init; }

    /// <summary>Session, in deren Kontext der Key verwendet wurde (mutierende Endpunkte, Spec §4.3).</summary>
    public required Ulid SessionId { get; init; }

    /// <summary>
    /// HTTP-Statuscode der ursprünglichen Antwort — Teil des Response-Snapshots, der bei
    /// Wiederholung identisch zurückgegeben wird (Spec §4.4).
    /// </summary>
    public required int ResponseStatusCode { get; init; }

    /// <summary>
    /// Snapshot des ursprünglichen Response-Bodys (kanonisches JSON). Bei wiederholtem Key wird
    /// genau dieser Body erneut geliefert (Spec §4.4: identische Antwort, kein Doppel-Append).
    /// </summary>
    public required string ResponseBody { get; init; }

    /// <summary>Zeitpunkt des ersten Eingangs (Audit; Basis der 24-h-Retention, Spec §4.4).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Ablaufzeitpunkt = <see cref="CreatedAt"/> + 24 h (Spec §4.4: Aufbewahrung 24 h). Trägt die
    /// Retention als Feld; das tatsächliche Löschen übernimmt ein späterer Cleanup-Job.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
