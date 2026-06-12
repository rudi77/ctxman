using Ctxman.Core;
using Ctxman.Core.Auth;
using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Api.Endpoints;

/// <summary>
/// Blob-Endpunkt (Spec §4.3 / §7): <c>POST /v1/sessions/{sid}/blobs</c> — Streaming-Upload eines
/// content-adressierten Inhalts. Gruppiert pro Ressource (CLAUDE.md). Der Tenant kommt ausschließlich
/// aus dem aufgelösten <see cref="ITenantContext"/> (Spec §10); der globale Query-Filter liefert für
/// unbekannte oder fremde Sessions null ⇒ 404 ohne Existenz-Leak. Blob-Upload ist content-addressed
/// und damit natürlich idempotent — kein Idempotency-Key nötig (Spec §4.3).
/// </summary>
public static class BlobEndpoints
{
    private const string DefaultContentType = "application/octet-stream";

    public static IEndpointRouteBuilder MapBlobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/sessions/{sid}/blobs", UploadBlobAsync)
            .WithMetadata(new ResourceAction("blob", null, "write")); // Spec §4.1
        return app;
    }

    private static async Task<IResult> UploadBlobAsync(
        string sid,
        HttpRequest httpRequest,
        CtxmanDbContext db,
        ITenantContext tenant,
        IBlobStore blobStore,
        CancellationToken ct)
    {
        if (!Ulid.TryParse(sid, out var sessionId))
        {
            // Ungültige ID kann keine Session adressieren — wie unbekannt behandeln (kein Leak).
            return Results.NotFound();
        }

        // Spec §10: globaler Query-Filter ⇒ unbekannte ODER fremde Session liefert false ⇒ 404.
        var exists = await db.Sessions
            .AsNoTracking()
            .AnyAsync(s => s.Id == sessionId, ct);
        if (!exists)
        {
            return Results.NotFound();
        }

        // Spec §7: Body als Stream lesen (kein vollständiges Puffern). Content-Type aus dem Header,
        // Default application/octet-stream falls nicht gesetzt.
        var contentType = string.IsNullOrWhiteSpace(httpRequest.ContentType)
            ? DefaultContentType
            : httpRequest.ContentType;

        var blobRef = await blobStore.Put(tenant.TenantId, httpRequest.Body, contentType, ct);

        return Results.Created((string?)null, new BlobUploadResponse(blobRef));
    }
}

// Spec §4.3: 201 mit { blob_ref: { store, key, size_bytes, content_type } } (snake_case via globale JSON-Options).
internal sealed record BlobUploadResponse(BlobRef BlobRef);
