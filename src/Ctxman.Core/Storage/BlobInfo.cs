namespace Ctxman.Core.Storage;

/// <summary>
/// Metadaten eines gespeicherten Blobs für die Enumeration via <see cref="IBlobStore.List"/> (Spec §7).
/// <paramref name="Key"/> ist der nackte sha256-Hash (lowercase hex), <paramref name="SizeBytes"/> die
/// physische Größe, <paramref name="LastModified"/> der Schreibzeitpunkt (UTC).
/// </summary>
public sealed record BlobInfo(string Key, long SizeBytes, DateTimeOffset LastModified);
