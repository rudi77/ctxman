using Ctxman.Core.Domain;

namespace Ctxman.Tests.Support;

/// <summary>
/// Gemeinsame, deterministische Fixtures für die WP4-Akzeptanztests (Spec §3 / §5 / §6 / §7.1).
/// Direkt-Seeding von Sessions/Segmenten über <see cref="CtxmanWebAppFactory.CreateDbContext"/>,
/// damit die Tests Watermark-Schwellen, TTL-Liveness und Externalisierungs-Grenzen exakt kreuzen,
/// ohne über viele Turns „hochzurendern". Token := ceil(chars/4) (HeuristicTokenCounter, §8), aber
/// die Tests setzen <see cref="Segment.Tokens"/> hier direkt, damit die Budget-Mathematik exakt ist.
/// </summary>
internal static class Wp4Fixtures
{
    private static long _seq;

    /// <summary>Server-vergebene, strikt monotone seq über alle Test-Segmente (Spec §2.2).</summary>
    public static long NextSeq() => Interlocked.Increment(ref _seq);

    /// <summary>
    /// Erzwingt den Host-Build (und damit das einmalige Schema-Setup in
    /// <see cref="CtxmanWebAppFactory.CreateHost"/>), bevor über <c>CreateDbContext</c> direkt
    /// geseedet wird. Ohne diesen Trigger existieren die Tabellen noch nicht, wenn ein Test seedet,
    /// bevor er einen Client/Service auflöst.
    /// </summary>
    public static void EnsureHost(CtxmanWebAppFactory factory) => _ = factory.Services;

    /// <summary>
    /// Policy mit kleinem Budget, damit Watermark-Schwellen (0.60/0.80/0.95·B) mit wenigen Tokens
    /// deterministisch gekreuzt werden. Externalize-Threshold bleibt frei wählbar.
    /// </summary>
    public static PolicyConfig SmallBudgetPolicy(int budgetTokens, int externalizeThresholdTokens = 2000) =>
        PolicyConfig.Default() with
        {
            BudgetTokens = budgetTokens,
            ExternalizeThresholdTokens = externalizeThresholdTokens,
        };

    /// <summary>Legt eine aktive Session mit der gegebenen Policy + Turn direkt in der DB an.</summary>
    public static Session NewSession(
        string tenantId,
        PolicyConfig policy,
        int currentTurn = 0,
        Ulid? id = null)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new Session(
            id: id ?? Ulid.NewUlid(),
            tenantId: tenantId,
            agentTemplateId: "tmpl-wp4",
            policy: policy,
            contextVersion: 1,
            staticEpoch: 0,
            currentTurn: currentTurn,
            status: SessionStatus.Active,
            createdAt: now,
            updatedAt: now);
    }

    /// <summary>Live-Working-Segment mit explizit gesetzten Tokens/TTL-Feldern (Spec §2.2 / §3.2).</summary>
    public static Segment LiveWorking(
        Ulid sessionId,
        string tenantId,
        string kind,
        int tokens,
        string content = "content",
        bool refetchable = false,
        bool pinned = false,
        string? origin = null,
        string? toolCallId = null,
        Role? role = Role.User,
        int createdTurn = 0,
        int lastReferencedTurn = 0) =>
        new(
            id: Ulid.NewUlid(),
            sessionId: sessionId,
            tenantId: tenantId,
            region: Region.Working,
            kind: kind,
            source: null,
            role: role,
            content: content,
            blobRef: null,
            refetchable: refetchable,
            origin: origin,
            summary: null,
            toolCallId: toolCallId,
            frameId: null,
            pinned: pinned,
            createdTurn: createdTurn,
            lastReferencedTurn: lastReferencedTurn,
            tokens: tokens,
            seq: NextSeq(),
            state: SegmentState.Live);
}
