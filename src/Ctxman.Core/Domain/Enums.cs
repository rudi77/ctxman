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

/// <summary>Render-Scope-Filter (Spec §2.5). Steuert, welche Working-Segmente gerendert werden.</summary>
public enum RenderScope
{
    /// <summary>Default: Static + Working-Segmente des aktuellen Frame-Pfads (Root + alle offenen Frames).</summary>
    Path,

    /// <summary>Isolierter Subagent-View: Static + gepinnte Root-Segmente + Segmente des Tip-Frames.</summary>
    Frame,
}
