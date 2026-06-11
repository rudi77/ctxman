using System.Text.Json;
using Ctxman.Core.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ctxman.Core.Persistence.Converters;

/// <summary>
/// Wiederverwendbare EF-Core-<see cref="ValueConverter"/>s für das ctxman-Domänenmodell.
/// Hier liegt die provider-neutrale Übersetzung zwischen Domänen-Typen und ihrer
/// Spalten-Repräsentation; die konkrete Entity-Konfiguration (Subtask 6) greift darauf zu.
///
/// Wire-Format folgt CLAUDE.md: ULIDs als kanonische 26-Zeichen-Strings, Enums als
/// snake_case-Strings über <see cref="EnumWire"/>, Value Objects (PolicyConfig, BlobRef)
/// als JSON mit <see cref="JsonNamingPolicy.SnakeCaseLower"/> (Spec-Wire-Format).
/// </summary>
public static class CtxmanValueConverters
{
    /// <summary>
    /// Kanonische JSON-Optionen für die als JSON gespeicherten Value Objects: snake_case-
    /// Property-Namen (CLAUDE.md / Spec-Wire-Format). Keine Einrückung — die Spalte hält
    /// kompaktes JSON.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary><see cref="System.Ulid"/> ⇔ kanonischer 26-Zeichen-String.</summary>
    public static readonly ValueConverter<Ulid, string> Ulid = new(
        id => id.ToString(),
        s => global::System.Ulid.Parse(s));

    /// <summary><see cref="System.Ulid"/>? ⇔ kanonischer 26-Zeichen-String (null bleibt null).</summary>
    public static readonly ValueConverter<Ulid?, string?> NullableUlid = new(
        id => id.HasValue ? id.Value.ToString() : null,
        s => s == null ? null : global::System.Ulid.Parse(s));

    /// <summary><see cref="Region"/> ⇔ snake_case-String (Spec §2.2).</summary>
    public static readonly ValueConverter<Region, string> Region = new(
        v => v.ToWire(),
        s => EnumWire.ParseRegion(s));

    /// <summary><see cref="Role"/> ⇔ snake_case-String (Spec §2.2).</summary>
    public static readonly ValueConverter<Role, string> Role = new(
        v => v.ToWire(),
        s => EnumWire.ParseRole(s));

    /// <summary><see cref="Role"/>? ⇔ snake_case-String (null bleibt null; Spec §2.2).</summary>
    public static readonly ValueConverter<Role?, string?> NullableRole = new(
        v => v.HasValue ? v.Value.ToWire() : null,
        s => s == null ? null : EnumWire.ParseRole(s));

    /// <summary><see cref="SegmentState"/> ⇔ snake_case-String (Spec §2.2).</summary>
    public static readonly ValueConverter<SegmentState, string> SegmentState = new(
        v => v.ToWire(),
        s => EnumWire.ParseSegmentState(s));

    /// <summary><see cref="SessionStatus"/> ⇔ snake_case-String (Spec §2.1).</summary>
    public static readonly ValueConverter<SessionStatus, string> SessionStatus = new(
        v => v.ToWire(),
        s => EnumWire.ParseSessionStatus(s));

    /// <summary><see cref="FrameStatus"/> ⇔ snake_case-String (Spec §2.5).</summary>
    public static readonly ValueConverter<FrameStatus, string> FrameStatus = new(
        v => v.ToWire(),
        s => EnumWire.ParseFrameStatus(s));

    /// <summary>
    /// <see cref="PolicyConfig"/> ⇔ JSON-String (snake_case, Spec §5). Die effektive Policy wird
    /// als Snapshot an der Session gespeichert (Reproduzierbarkeit, §2.1).
    /// </summary>
    public static readonly ValueConverter<PolicyConfig, string> PolicyConfig = new(
        v => JsonSerializer.Serialize(v, JsonOptions),
        s => JsonSerializer.Deserialize<PolicyConfig>(s, JsonOptions)!);

    /// <summary>
    /// <see cref="BlobRef"/>? ⇔ JSON-String (snake_case, Spec §2.6). Das verschachtelte Value
    /// Object wird als JSON gespeichert (null bleibt null).
    /// </summary>
    public static readonly ValueConverter<BlobRef?, string?> NullableBlobRef = new(
        v => v == null ? null : JsonSerializer.Serialize(v, JsonOptions),
        s => s == null ? null : JsonSerializer.Deserialize<BlobRef>(s, JsonOptions));
}
