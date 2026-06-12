using Ctxman.Api.Gc;
using Ctxman.Core;
using Ctxman.Core.Auth;
using Ctxman.Core.Gc;
using Ctxman.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Endpoints;

/// <summary>
/// GC-Endpunkt (Spec §4.3): <c>POST /v1/sessions/{sid}/gc</c> reiht einen GC-Lauf ein und
/// antwortet <c>202 { job_id }</c>. Minor- und Major-Läufe laufen asynchron außerhalb des
/// Request-Pfads (Spec §8); der Worker serialisiert pro session_id. Der Tenant kommt
/// ausschließlich aus dem aufgelösten <see cref="ITenantContext"/> (Spec §10); der globale
/// Query-Filter liefert für unbekannte oder fremde Sessions null ⇒ 404 ohne Existenz-Leak.
/// </summary>
public static class GcEndpoints
{
    public static IEndpointRouteBuilder MapGcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/sessions/{sid}/gc", TriggerGcAsync)
            .WithMetadata(new ResourceAction("gc", null, "write")); // Spec §4.1
        return app;
    }

    private static async Task<IResult> TriggerGcAsync(
        string sid,
        GcRequest request,
        CtxmanDbContext db,
        ITenantContext tenant,
        IGcJobQueue queue,
        CancellationToken ct)
    {
        // Spec §4.3: unbekannter level-String ⇒ 400 vor jeder Existenzprüfung.
        GcLevel level;
        try
        {
            level = GcLevelWire.ParseGcLevel(request.Level ?? string.Empty);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = $"Unknown gc level '{request.Level}'." });
        }

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

        // Spec §3.2 / §3.3 / §8: Minor- wie Major-Lauf werden als Job eingereiht; der Worker
        // wertet den Level aus. job_id ist eine frische ULID, wird in der Antwort zurückgegeben.
        var jobId = Ulid.NewUlid();
        queue.Enqueue(new MinorGcJob(tenant.TenantId, session.Id, jobId, level));

        return Results.Accepted(
            $"/v1/sessions/{session.Id}/gc",
            new GcResponse(jobId.ToString()));
    }
}
