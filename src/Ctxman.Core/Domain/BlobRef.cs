namespace Ctxman.Core.Domain;

/// <summary>
/// Pointer auf einen externalisierten, content-adressierten Inhalt im Blob Store (Spec §2.6).
/// Blob-Inhalte sind immutable (key = sha256 des Inhalts); BlobRefs haben keine eigene Identität.
/// </summary>
public sealed record BlobRef(
    string Store,
    string Key,
    long SizeBytes,
    string ContentType);
