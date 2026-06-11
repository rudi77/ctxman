namespace Ctxman.Core.Persistence;

/// <summary>
/// Eine Zeile der <c>events</c>-Outbox (Spec §7 Tabellen / §10: append-only, unveränderlich).
/// Jede Mutation und jede GC-Operation emittiert ein Event (Spec §6); der Stream ist über
/// <c>GET /v1/sessions/{sid}/events?after_seq=…</c> (Spec §4.3) per <see cref="Seq"/> abrufbar.
///
/// Reines Persistenz-DTO ohne Domänenlogik — Outbox-Zeilen werden nie geändert (§10), daher
/// init-only Properties. Die snake_case-Tabellen-/Spaltennamen setzt die Entity-Konfiguration
/// (Subtask 6); der <see cref="Payload"/> ist bereits kanonisches snake_case-JSON (§6).
/// </summary>
public sealed class EventRecord
{
    /// <summary>Eindeutige Event-ID (ULID).</summary>
    public required Ulid Id { get; init; }

    /// <summary>Tenant-Isolation auf Zeilenebene (Spec §7 / §10); kommt nie aus dem Body.</summary>
    public required string TenantId { get; init; }

    /// <summary>Session, zu der das Event gehört (Spec §4.3: events sind session-gescoped).</summary>
    public required Ulid SessionId { get; init; }

    /// <summary>
    /// Event-Typ (Spec §6), z. B. <c>segment_appended</c>, <c>static_epoch_bumped</c>,
    /// <c>render_served</c>. Offenes Vokabular als snake_case-String.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Event-Payload als kanonisches JSON (Spec §6, snake_case). Typ-spezifische Felder
    /// (z. B. <c>{ old_epoch, new_epoch, tokens_delta, diff }</c> bei <c>static_epoch_bumped</c>)
    /// liegen hier als JSON-String — schema-flexibel und audit-stabil.
    /// </summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Monotone Sequenznummer innerhalb der Session — stabile Reihenfolge für den Pull-Cursor
    /// (Spec §4.3: <c>after_seq</c>).
    /// </summary>
    public required long Seq { get; init; }

    /// <summary>Erzeugungszeitpunkt des Events (Audit-Trail, Spec §6 / §10).</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
