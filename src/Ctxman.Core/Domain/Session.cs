namespace Ctxman.Core.Domain;

/// <summary>
/// Repräsentiert den Context eines Agent-Laufs (Spec §2.1). Die Session ist Träger der
/// optimistic-concurrency-Version (<see cref="ContextVersion"/>), der Static-Epoche
/// (<see cref="StaticEpoch"/>, siehe I1 / §4.2) und des aktuellen Turns. Die effektive Policy
/// wird bei der Erstellung als Snapshot eingefroren (Reproduzierbarkeit, §5).
///
/// Mutable <c>sealed class</c>: Version, Epoche, Turn und Status entwickeln sich über die
/// Lebensdauer der Session.
/// </summary>
public sealed class Session
{
    // EF Core kann eine Navigation-Collection nicht an einen Konstruktor-Parameter binden;
    // daher nimmt der Konstruktor nur skalare Felder (§2.1) und EF befüllt _frames über das
    // Backing-Field (siehe CtxmanDbContext: Navigation.HasField("_frames")).
    private readonly List<Frame> _frames = new();

    public Session(
        Ulid id,
        string tenantId,
        string? agentTemplateId,
        PolicyConfig policy,
        long contextVersion,
        int staticEpoch,
        int currentTurn,
        SessionStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        TenantId = tenantId;
        AgentTemplateId = agentTemplateId;
        Policy = policy;
        ContextVersion = contextVersion;
        StaticEpoch = staticEpoch;
        CurrentTurn = currentTurn;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>ULID der Session (Spec §2.1).</summary>
    public Ulid Id { get; }

    /// <summary>Pflicht; Isolation auf Zeilenebene (Spec §2.1 / §10); kommt nie aus dem Body.</summary>
    public string TenantId { get; }

    /// <summary>Referenziert die Policy-Konfiguration; null im Standalone-Setup (Spec §2.1).</summary>
    public string? AgentTemplateId { get; }

    /// <summary>Aufgelöste, effektive Policy — Snapshot bei Erstellung (Spec §2.1 / §5).</summary>
    public PolicyConfig Policy { get; }

    /// <summary>Monoton, optimistic concurrency (Spec §2.1 / §4.4).</summary>
    public long ContextVersion { get; private set; }

    /// <summary>Version der Static-Region (Spec §2.1, siehe I1 und §4.2).</summary>
    public int StaticEpoch { get; private set; }

    /// <summary>Aktueller Turn (Spec §2.1); ein Turn = ein Model-Call (§3).</summary>
    public int CurrentTurn { get; private set; }

    /// <summary>active | archived (Spec §2.1).</summary>
    public SessionStatus Status { get; private set; }

    /// <summary>Erstellungszeitpunkt (Spec §2.1).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Letzter Änderungszeitpunkt (Spec §2.1).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Frames der Session (Spec §2.1: <c>frames: Stack&lt;Frame&gt;</c>). Stack-Disziplin (LIFO,
    /// §2.5) wird auf höherer Ebene erzwungen; hier die kanonische, lesbare Sammlung.
    /// </summary>
    public IReadOnlyList<Frame> Frames => _frames;

    /// <summary>
    /// Fügt einen Frame zur Session hinzu (Stack-Disziplin LIFO, §2.5 wird auf höherer Ebene
    /// erzwungen). Ersetzt den früheren Konstruktor-Parameter, da EF die Navigation nicht über
    /// den Konstruktor binden kann.
    /// </summary>
    public void AddFrame(Frame frame) => _frames.Add(frame);

    /// <summary>
    /// Erhöht <see cref="ContextVersion"/> um 1 und setzt <see cref="UpdatedAt"/> auf
    /// <paramref name="now"/>. Einziger Ort, der die Monotonie der context_version garantiert
    /// (Spec §4.4).
    /// </summary>
    public void IncrementVersion(DateTimeOffset now)
    {
        ContextVersion += 1; // Spec §4.4
        UpdatedAt = now;
    }

    /// <summary>
    /// Erhöht <see cref="CurrentTurn"/> um 1 und setzt <see cref="UpdatedAt"/> auf
    /// <paramref name="now"/>. Einziger Ort, der die Monotonie des current_turn garantiert
    /// (Spec §4.4).
    /// </summary>
    public void AdvanceTurn(DateTimeOffset now)
    {
        CurrentTurn += 1; // Spec §4.4
        UpdatedAt = now;
    }

    /// <summary>
    /// Erhöht <see cref="StaticEpoch"/> um 1 und setzt <see cref="UpdatedAt"/> auf
    /// <paramref name="now"/> (Spec §4.2, siehe I1). Erhöht bewusst nicht die
    /// context_version — der Aufrufer ruft dafür separat <see cref="IncrementVersion"/>.
    /// </summary>
    public void BumpStaticEpoch(DateTimeOffset now)
    {
        StaticEpoch += 1; // Spec §4.2
        UpdatedAt = now;
    }
}
