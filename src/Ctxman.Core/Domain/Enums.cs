namespace Ctxman.Core.Domain;

/// <summary>Region eines Segments. Spec §2.2: <c>Static | Working</c>.</summary>
public enum Region
{
    Static,
    Working,
}

/// <summary>Rolle eines Segments. Spec §2.2: <c>system | user | assistant | tool</c>.</summary>
public enum Role
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>Lebenszyklus-Zustand eines Segments. Spec §2.2: <c>live | externalized | compacted | evicted</c>.</summary>
public enum SegmentState
{
    Live,
    Externalized,
    Compacted,
    Evicted,
}

/// <summary>Status einer Session. Spec §2.1: <c>active | archived</c>.</summary>
public enum SessionStatus
{
    Active,
    Archived,
}

/// <summary>Status eines Frames. Spec §2.5: <c>open | popped</c>.</summary>
public enum FrameStatus
{
    Open,
    Popped,
}

/// <summary>Auth-Modus der Tenant-Auflösung. Spec §4.1: <c>none | api_key | jwt</c>.</summary>
public enum AuthMode
{
    None,
    ApiKey,
    Jwt,
}
