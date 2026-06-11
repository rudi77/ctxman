using System.Text;
using System.Text.Json;
using Ctxman.Core;
using Ctxman.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Ctxman.Api.Idempotency;

/// <summary>
/// Wiederverwendbare Replay-/Store-Logik für den <c>Idempotency-Key</c> (Spec §4.4). Mutierende
/// Endpunkte schlagen vor dem Write nach, ob für <c>(tenant_id, key)</c> bereits ein noch gültiger
/// (<see cref="IdempotencyKeyRecord.ExpiresAt"/> &gt; now) Snapshot existiert; falls ja, wird die
/// gespeicherte Antwort byte-genau erneut geliefert, ohne ein zweites Mal zu schreiben. Abgelaufene
/// Einträge gelten als nicht vorhanden (Lazy-Cleanup, Spec §4.4: Aufbewahrung 24 h).
///
/// Der Body-Snapshot wird mit denselben <see cref="JsonSerializerOptions"/> serialisiert, die die App
/// für die HTTP-Antworten nutzt (snake_case, <see cref="JsonOptions"/>); damit ist ein Replay-Body
/// byte-identisch zur ursprünglichen Antwort.
/// </summary>
public sealed class IdempotencyService
{
    // Spec §4.4: Aufbewahrung 24 h.
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly CtxmanDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly JsonSerializerOptions _jsonOptions;

    public IdempotencyService(
        CtxmanDbContext db,
        ITenantContext tenant,
        IOptions<JsonOptions> jsonOptions)
    {
        _db = db;
        _tenant = tenant;
        // Dieselben Optionen, mit denen das Framework die Endpoint-Bodies serialisiert
        // (Program.cs: ConfigureHttpJsonOptions ⇒ snake_case) — so ist der gespeicherte Body
        // byte-identisch zur ursprünglich gesendeten Antwort.
        _jsonOptions = jsonOptions.Value.SerializerOptions;
    }

    /// <summary>
    /// Sucht einen noch gültigen Snapshot für <paramref name="key"/> im aktuellen Tenant. Liefert
    /// bei einem Treffer ein <see cref="IResult"/>, das die gespeicherte Antwort byte-genau erneut
    /// schreibt (gleicher Statuscode, gleiche Bytes, <c>application/json</c>); sonst <c>null</c>.
    /// Abgelaufene Einträge (Spec §4.4) werden ignoriert.
    /// </summary>
    public async Task<IResult?> TryReplayAsync(string key, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Tenant-Isolation über den Global Query Filter (Spec §10); zusätzlich auf den Key gefiltert.
        var record = await _db.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == key, ct);

        if (record is null || record.ExpiresAt <= now)
        {
            // Miss oder abgelaufen ⇒ als nicht vorhanden behandeln (Lazy-Cleanup, Spec §4.4).
            return null;
        }

        return new ReplayResult(record.ResponseStatusCode, record.ResponseBody);
    }

    /// <summary>
    /// Hängt einen <see cref="IdempotencyKeyRecord"/> für <paramref name="key"/> an den
    /// EF-ChangeTracker an, OHNE zu speichern. Der Aufrufer committet ihn in derselben Transaktion
    /// wie die eigentliche Mutation (Spec §4.4: atomar — ein Replay findet stets einen konsistenten
    /// Snapshot). Existiert für <c>(tenant_id, key)</c> bereits ein (i. d. R. abgelaufener) Eintrag,
    /// wird dieser per Upsert ersetzt statt ein Duplikat einzufügen — andernfalls verletzte ein
    /// erneutes Append nach Ablauf den zusammengesetzten Primärschlüssel (Lazy-Cleanup, Spec §4.4).
    /// <paramref name="responseBody"/> wird mit den App-JSON-Optionen serialisiert, damit der
    /// gespeicherte Body byte-identisch zur gesendeten Antwort ist.
    /// </summary>
    public async Task StageRecordAsync(
        string key,
        Ulid sessionId,
        int statusCode,
        object responseBody,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var body = JsonSerializer.Serialize(responseBody, _jsonOptions);

        // Tracked laden (kein AsNoTracking) — ein vorhandener Eintrag wird im selben
        // ChangeTracker entfernt, bevor der neue eingefügt wird (Upsert).
        // Tenant-Isolation über den Global Query Filter (Spec §10).
        var existing = await _db.IdempotencyKeys.FirstOrDefaultAsync(k => k.Key == key, ct);

        if (existing is not null)
        {
            // Abgelaufenen/veralteten Snapshot ersetzen (Lazy-Cleanup, Spec §4.4) statt ein Duplikat
            // einzufügen — Letzteres verletzte den PK (tenant_id, key). Remove+Add läuft in derselben
            // Transaktion wie das Append, der Replay findet stets einen konsistenten Snapshot.
            _db.IdempotencyKeys.Remove(existing);
        }

        _db.IdempotencyKeys.Add(new IdempotencyKeyRecord
        {
            Key = key,
            TenantId = _tenant.TenantId, // Spec §10: nur aus dem TenantContext, nie aus dem Body.
            SessionId = sessionId,
            ResponseStatusCode = statusCode,
            ResponseBody = body,
            CreatedAt = now,
            ExpiresAt = now + Retention,
        });
    }

    /// <summary>
    /// Schreibt einen gespeicherten Response-Snapshot byte-genau zurück: identischer Statuscode,
    /// identische Body-Bytes, <c>application/json</c> (Spec §4.4: identische Antwort beim Replay).
    /// </summary>
    private sealed class ReplayResult : IResult
    {
        private readonly int _statusCode;
        private readonly string _body;

        public ReplayResult(int statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = _statusCode;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await httpContext.Response.WriteAsync(_body, Encoding.UTF8);
        }
    }
}
