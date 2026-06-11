namespace Ctxman.Core.Domain;

/// <summary>
/// Bildet einen Subagent-Aufruf als Stack-Frame ab (Spec §2.5). Neue Segmente erhalten die
/// <see cref="Id"/> des obersten offenen Frames; ein <c>pop</c> evicted alle Segmente des Frames
/// und legt ein <c>subagent_return</c>-Segment im Parent-Frame an. LIFO-Disziplin (§2.5).
///
/// Mutable <c>sealed class</c>: der Status wechselt von open → popped über <see cref="Pop"/>.
/// </summary>
public sealed class Frame
{
    public Frame(
        Ulid id,
        Ulid sessionId,
        string tenantId,
        Ulid? parentFrameId,
        string label,
        int openedTurn,
        FrameStatus status = FrameStatus.Open)
    {
        Id = id;
        SessionId = sessionId;
        TenantId = tenantId;
        ParentFrameId = parentFrameId;
        Label = label;
        OpenedTurn = openedTurn;
        Status = status;
    }

    /// <summary>ULID des Frames (Spec §2.5).</summary>
    public Ulid Id { get; }

    /// <summary>Session, zu der der Frame gehört (Spec §2.5).</summary>
    public Ulid SessionId { get; }

    /// <summary>Tenant-Isolation auf Zeilenebene (Spec §10); kommt nie aus dem Body.</summary>
    public string TenantId { get; }

    /// <summary>null = Root-Frame (Spec §2.5).</summary>
    public Ulid? ParentFrameId { get; }

    /// <summary>Label, z. B. "research_subtask" (Spec §2.5).</summary>
    public string Label { get; }

    /// <summary>Turn, in dem der Frame geöffnet wurde (Spec §2.5).</summary>
    public int OpenedTurn { get; }

    /// <summary>open | popped (Spec §2.5).</summary>
    public FrameStatus Status { get; private set; }

    /// <summary>Markiert den Frame als gepoppt (Spec §2.5 pop).</summary>
    public void Pop() => Status = FrameStatus.Popped;
}
