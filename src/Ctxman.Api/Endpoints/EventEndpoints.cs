using System.Text;
using System.Text.Json;
using Ctxman.Core;
using Ctxman.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Endpoints;

/// <summary>
/// Event-Outbox-Endpunkt (Spec §4.3 / §6): <c>GET /v1/sessions/{sid}/events?after_seq=…</c>.
/// Liefert die session-gescopten Events mit <c>seq &gt; after_seq</c> aufsteigend, entweder als
/// Pull (<c>200 { events[] }</c>) oder als SSE-Stream (<c>Accept: text/event-stream</c> bzw.
/// <c>?stream=true</c>). Der Tenant kommt ausschließlich aus dem aufgelösten
/// <see cref="ITenantContext"/> bzw. dem globalen Query-Filter (Spec §10); unbekannte oder fremde
/// Session-IDs liefern null ⇒ 404 ohne Existenz-Leak.
/// </summary>
public static class EventEndpoints
{
    // Spec §6: Event-Payload ist kanonisches snake_case-JSON; die Item-Hülle (seq/type/payload/
    // created_at) wird mit derselben Policy serialisiert.
    private static readonly JsonSerializerOptions EventResponseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/sessions/{sid}/events", GetEventsAsync);
        return app;
    }

    private static async Task<IResult> GetEventsAsync(
        string sid,
        long? after_seq,
        bool? stream,
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        CtxmanDbContext db,
        CancellationToken ct)
    {
        if (!Ulid.TryParse(sid, out var sessionId))
        {
            // Ungültige ID kann keine Session adressieren — wie unbekannt behandeln (kein Leak).
            return Results.NotFound();
        }

        // Spec §10: globaler Query-Filter ⇒ unbekannte ODER fremde Session liefert null ⇒ 404.
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        // Spec §4.3: after_seq ist ein exklusiver Cursor mit Default 0 ⇒ alle Events ab seq 1.
        var afterSeq = after_seq ?? 0;

        // Spec §10: zusätzlich zum globalen Tenant-Filter noch session-gescopt; aufsteigend
        // nach seq für einen stabilen Cursor (Spec §6: seq ist pro Session monoton).
        var records = await db.Events
            .Where(ev => ev.SessionId == sessionId && ev.Seq > afterSeq)
            .OrderBy(ev => ev.Seq)
            .ToListAsync(ct);

        // Spec §4.3: SSE-Variante bei Accept: text/event-stream oder ?stream=true.
        var wantsSse = (stream ?? false)
            || httpRequest.Headers.Accept.Any(static a =>
                a is not null && a.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase));

        if (wantsSse)
        {
            return new EventStreamResult(records);
        }

        var events = records.Select(ToEventItem).ToList();
        return Results.Json(new { events }, EventResponseOptions);
    }

    // Spec §6: Event-Item ist { seq, type, payload, created_at } (+ id). payload ist bereits
    // kanonisches JSON und wird als JsonElement eingebettet — also als strukturiertes JSON-Objekt
    // ausgegeben, nicht als re-stringifizierter String.
    private static object ToEventItem(EventRecord record) => new
    {
        id = record.Id.ToString(),
        seq = record.Seq,
        type = record.Type,
        payload = ParsePayload(record.Payload),
        created_at = record.CreatedAt,
    };

    private static JsonElement ParsePayload(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// SSE-Antwort (Spec §4.3): schreibt den aktuell vorhandenen Backlog (seq &gt; after_seq) als
    /// SSE-Frames (<c>id: {seq}\ndata: {json}\n\n</c>) und schließt danach den Stream. Bounded und
    /// nicht-hängend, damit Tests die Frames ohne Timeout lesen können.
    /// </summary>
    private sealed class EventStreamResult : IResult
    {
        private readonly IReadOnlyList<EventRecord> _records;

        public EventStreamResult(IReadOnlyList<EventRecord> records) => _records = records;

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";

            var ct = httpContext.RequestAborted;
            var builder = new StringBuilder();

            foreach (var record in _records)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var data = JsonSerializer.Serialize(ToEventItem(record), EventResponseOptions);

                builder.Clear();
                builder.Append("id: ").Append(record.Seq).Append('\n');
                builder.Append("data: ").Append(data).Append("\n\n");

                await response.WriteAsync(builder.ToString(), ct);
                await response.Body.FlushAsync(ct);
            }
        }
    }
}
