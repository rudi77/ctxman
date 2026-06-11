namespace Ctxman.Core.Gc;

/// <summary>
/// Granularität eines GC-Laufs (Spec §3.2 / §3.3): <c>minor | major</c>.
/// Minor = TTL-Eviction/Externalisierung im Hot Path, Major = Compaction/Promotion.
/// </summary>
public enum GcLevel
{
    Minor,
    Major,
}

/// <summary>
/// snake_case-Wire-Abbildung für <see cref="GcLevel"/> (Wire-Format wie in CLAUDE.md);
/// folgt dem Muster von <c>EnumWire</c> im Domain-Namespace.
/// </summary>
public static class GcLevelWire
{
    public static string ToWire(this GcLevel value) => value switch
    {
        GcLevel.Minor => "minor",
        GcLevel.Major => "major",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unbekannter GcLevel-Wert."),
    };

    public static GcLevel ParseGcLevel(string wire) => wire switch
    {
        "minor" => GcLevel.Minor,
        "major" => GcLevel.Major,
        _ => throw new ArgumentException($"Unbekannter Wire-Wert '{wire}' für GcLevel.", nameof(wire)),
    };
}
