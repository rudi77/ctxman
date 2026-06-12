namespace Ctxman.Api.Storage;

/// <summary>
/// Konfiguration des Cold-Storage-Exporters (Spec §7.1).
/// Konfigurations-Sektion: <c>cold_storage</c>.
/// </summary>
public sealed class ColdStorageOptions
{
    public const string SectionName = "cold_storage";

    /// <summary>Wurzelverzeichnis, unter dem Cold-Storage-Blobs tenant-präfixiert abgelegt werden.</summary>
    public string Root { get; set; } = string.Empty;
}
