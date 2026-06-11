namespace Ctxman.Core.Domain;

/// <summary>
/// Das atomare Element des Contexts (Spec §2.2). Die Message-Liste der Provider-API wird
/// ausschließlich aus Segmenten gerendert; das Segment ist Source of Truth, die Message-Liste
/// ein Render-Artefakt (Spec §1.1).
///
/// Bewusst eine mutable <c>sealed class</c> (kein record): Segmente durchlaufen einen
/// Lebenszyklus (live → externalized | compacted | evicted) und tragen die Invarianten
/// I1–I3 als Verhalten, nicht nur als Daten. Zustandsübergänge laufen ausschließlich über
/// die Guard-Methoden, damit die Invarianten an einer Stelle erzwungen werden.
/// </summary>
public sealed class Segment
{
    /// <summary>
    /// Erzeugt ein neues Segment und erzwingt I2 (Spec §2.2): bei state = live | externalized
    /// dürfen <paramref name="content"/> und <paramref name="blobRef"/> nicht beide null sein.
    /// Für die übliche Konstruktion stehen die Factories <see cref="CreateLive"/> und
    /// <see cref="CreateExternalized"/> bereit.
    /// </summary>
    public Segment(
        Ulid id,
        Ulid sessionId,
        string tenantId,
        Region region,
        string kind,
        string? source,
        Role? role,
        string? content,
        BlobRef? blobRef,
        bool refetchable,
        string? origin,
        string? summary,
        string? toolCallId,
        Ulid? frameId,
        bool pinned,
        int createdTurn,
        int lastReferencedTurn,
        int tokens,
        long seq,
        SegmentState state)
    {
        Id = id;
        SessionId = sessionId;
        TenantId = tenantId;
        Region = region;
        Kind = kind;
        Source = source;
        Role = role;
        Content = content;
        BlobRef = blobRef;
        Refetchable = refetchable;
        Origin = origin;
        Summary = summary;
        ToolCallId = toolCallId;
        FrameId = frameId;
        Pinned = pinned;
        CreatedTurn = createdTurn;
        LastReferencedTurn = lastReferencedTurn;
        Tokens = tokens;
        Seq = seq;
        State = state;

        GuardContentInvariant(); // Spec §2.2 I2
    }

    /// <summary>ULID des Segments (Spec §2.2).</summary>
    public Ulid Id { get; }

    /// <summary>Session, zu der das Segment gehört (Spec §2.2).</summary>
    public Ulid SessionId { get; }

    /// <summary>Tenant-Isolation auf Zeilenebene (Spec §10); kommt nie aus dem Body.</summary>
    public string TenantId { get; }

    /// <summary>Static | Working (Spec §2.2).</summary>
    public Region Region { get; }

    /// <summary>Offenes Vokabular (Spec §2.2 / §2.3).</summary>
    public string Kind { get; }

    /// <summary>Logische Herkunft, z. B. "core", "mcp:github" (Spec §2.2); Basis für Epoch-Diffs.</summary>
    public string? Source { get; }

    /// <summary>system | user | assistant | tool (Spec §2.2).</summary>
    public Role? Role { get; }

    /// <summary>Inhalt; null ⇔ externalisiert (Spec §2.2). Nur via Guard-Methoden veränderbar.</summary>
    public string? Content { get; private set; }

    /// <summary>Pointer in den Blob Store (Spec §2.2 / §2.6). Nur via Guard-Methoden veränderbar.</summary>
    public BlobRef? BlobRef { get; private set; }

    /// <summary>Inhalt verlustfrei aus externer Quelle neu beziehbar (Spec §2.2).</summary>
    public bool Refetchable { get; }

    /// <summary>Quell-URI bei refetchable, z. B. skill://… (Spec §2.2).</summary>
    public string? Origin { get; }

    /// <summary>Typ-Signatur bei Externalisierung; Kurzfassung nach Compaction (Spec §2.2).</summary>
    public string? Summary { get; private set; }

    /// <summary>Korrelation tool_use ↔ tool_result (Spec §2.2 / §2.4).</summary>
    public string? ToolCallId { get; }

    /// <summary>null = Root-Frame (Spec §2.2 / §2.5).</summary>
    public Ulid? FrameId { get; }

    /// <summary>Für GC unantastbar; rendert an chronologischer Position (Spec §2.2 / I4).</summary>
    public bool Pinned { get; private set; }

    /// <summary>Turn, in dem das Segment angelegt wurde (Spec §2.2).</summary>
    public int CreatedTurn { get; }

    /// <summary>Zuletzt referenzierter Turn; Basis für TTL-Liveness (Spec §2.2 / §3.2). Nur via Guard.</summary>
    public int LastReferencedTurn { get; private set; }

    /// <summary>Gezählt beim Append, Tokenizer konfigurierbar (Spec §2.2).</summary>
    public int Tokens { get; }

    /// <summary>Globale, stabile Render-Reihenfolge innerhalb der Session (Spec §2.2 / I4).</summary>
    public long Seq { get; }

    /// <summary>live | externalized | compacted | evicted (Spec §2.2). Nur via Guard-Methoden.</summary>
    public SegmentState State { get; private set; }

    /// <summary>
    /// Factory für ein reguläres, inline gehaltenes Segment (state = live).
    /// Erzwingt I2 über den Konstruktor.
    /// </summary>
    public static Segment CreateLive(
        Ulid id,
        Ulid sessionId,
        string tenantId,
        Region region,
        string kind,
        Role? role,
        string content,
        long seq,
        int createdTurn,
        string? source = null,
        bool refetchable = false,
        string? origin = null,
        string? toolCallId = null,
        Ulid? frameId = null,
        bool pinned = false,
        int tokens = 0,
        string? summary = null) =>
        new(
            id: id,
            sessionId: sessionId,
            tenantId: tenantId,
            region: region,
            kind: kind,
            source: source,
            role: role,
            content: content,
            blobRef: null,
            refetchable: refetchable,
            origin: origin,
            summary: summary,
            toolCallId: toolCallId,
            frameId: frameId,
            pinned: pinned,
            createdTurn: createdTurn,
            lastReferencedTurn: createdTurn,
            tokens: tokens,
            seq: seq,
            state: SegmentState.Live);

    /// <summary>
    /// Factory für ein von Anfang an externalisiertes Segment (state = externalized) — der
    /// Upload-Pfad referenziert großen Inhalt direkt per blob_ref + summary (Spec §4.3).
    /// Erzwingt I2 über den Konstruktor.
    /// </summary>
    public static Segment CreateExternalized(
        Ulid id,
        Ulid sessionId,
        string tenantId,
        Region region,
        string kind,
        Role? role,
        BlobRef blobRef,
        string? summary,
        long seq,
        int createdTurn,
        string? source = null,
        bool refetchable = false,
        string? origin = null,
        string? toolCallId = null,
        Ulid? frameId = null,
        bool pinned = false,
        int tokens = 0) =>
        new(
            id: id,
            sessionId: sessionId,
            tenantId: tenantId,
            region: region,
            kind: kind,
            source: source,
            role: role,
            content: null,
            blobRef: blobRef,
            refetchable: refetchable,
            origin: origin,
            summary: summary,
            toolCallId: toolCallId,
            frameId: frameId,
            pinned: pinned,
            createdTurn: createdTurn,
            lastReferencedTurn: createdTurn,
            tokens: tokens,
            seq: seq,
            state: SegmentState.Externalized);

    /// <summary>
    /// Externalisiert das Segment (Minor GC, Spec §3.2 Schritt 2): Inhalt wandert atomar in den
    /// Blob Store, content := null, blob_ref := ref, summary := summary, state := externalized.
    /// Da blob_ref gesetzt wird, bleibt I2 (§2.2) gewahrt. I1 (§2.2): auf Static-Segmenten verboten.
    /// </summary>
    public void Externalize(BlobRef blobRef, string? summary)
    {
        ArgumentNullException.ThrowIfNull(blobRef);
        GuardNotStatic(); // Spec §2.2 I1

        Content = null;
        BlobRef = blobRef;
        Summary = summary;
        State = SegmentState.Externalized;
        // I2 bleibt gewahrt: blob_ref != null.
    }

    /// <summary>
    /// Soft-Delete via Eviction (Spec §2.2 I3 / §3.2 Schritt 1+3): nur state := evicted.
    /// Daten (content/blob_ref/summary/origin) bleiben für den Audit-Trail erhalten — das Segment
    /// bleibt queryable, erscheint aber nie wieder im Render-Output. I1 (§2.2): auf Static verboten.
    /// </summary>
    public void Evict()
    {
        GuardNotStatic(); // Spec §2.2 I1
        State = SegmentState.Evicted;
        // I3: keine Daten entfernen — reiner Zustandsübergang.
    }

    /// <summary>
    /// Soft-Delete via Compaction (Spec §2.2 I3 / §3.3 Schritt 2): nur state := compacted,
    /// optional summary aktualisieren. Daten bleiben für den Audit-Trail erhalten; das Quell-Segment
    /// erscheint nie wieder im Render-Output. I1 (§2.2): auf Static verboten.
    /// </summary>
    public void Compact(string? summary = null)
    {
        GuardNotStatic(); // Spec §2.2 I1

        if (summary is not null)
        {
            Summary = summary;
        }

        State = SegmentState.Compacted;
        // I3: content/blob_ref bleiben als Audit-Metadatum erhalten — kein Datenverlust.
    }

    /// <summary>
    /// Aktualisiert last_referenced_turn (Spec §3.4: Page Fault setzt approximierte Liveness).
    /// I1 (§2.2): Static-Segmente sind immutable — Update verboten.
    /// </summary>
    public void MarkReferenced(int turn)
    {
        GuardNotStatic(); // Spec §2.2 I1
        LastReferencedTurn = turn;
    }

    /// <summary>Pinnt das Segment (Spec §4.3). I1 (§2.2): auf Static verboten.</summary>
    public void Pin()
    {
        GuardNotStatic(); // Spec §2.2 I1
        Pinned = true;
    }

    /// <summary>Entfernt den Pin (Spec §4.3). I1 (§2.2): auf Static verboten.</summary>
    public void Unpin()
    {
        GuardNotStatic(); // Spec §2.2 I1
        Pinned = false;
    }

    private void GuardNotStatic()
    {
        if (Region == Region.Static)
        {
            throw new StaticRegionImmutableException(Id);
        }
    }

    private void GuardContentInvariant()
    {
        // Spec §2.2 I2: content und blob_ref sind nie beide null bei state = live | externalized.
        if (State is SegmentState.Live or SegmentState.Externalized
            && Content is null && BlobRef is null)
        {
            throw new SegmentContentInvariantException(State);
        }
    }
}
