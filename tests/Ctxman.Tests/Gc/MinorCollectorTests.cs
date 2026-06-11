using Ctxman.Core.Domain;
using Ctxman.Core.Gc;

namespace Ctxman.Tests.Gc;

/// <summary>
/// Vertrag der deterministischen, I/O-freien Minor Collection (Spec §3.2 / §3.1 / §2.4).
/// Die Tests halten das Spec-Verhalten fest: Clean-Page-Eviction ohne Blob-Write (AC2),
/// TTL-Eviction respektiert pinned/Static (AC4), Unit-Kopplung tool_call↔tool_result (AC5)
/// und die I/O-freie Emergency-Untermenge (kein Phase-2-Externalisieren). In-Memory-Fixture,
/// kein DB/I/O.
/// </summary>
public sealed class MinorCollectorTests
{
    private static readonly Ulid SessionId = Ulid.NewUlid();
    private const string Tenant = "t1";

    private static long _seq;

    private static Segment Seg(
        string kind,
        Region region = Region.Working,
        bool refetchable = false,
        bool pinned = false,
        string? origin = null,
        string? toolCallId = null,
        int tokens = 0,
        int createdTurn = 0,
        int lastReferencedTurn = 0,
        string content = "content")
    {
        var seg = new Segment(
            id: Ulid.NewUlid(),
            sessionId: SessionId,
            tenantId: Tenant,
            region: region,
            kind: kind,
            source: null,
            role: Role.Tool,
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
            seq: Interlocked.Increment(ref _seq),
            state: SegmentState.Live);
        return seg;
    }

    // AC2: Clean-Page-Eviction → state=evicted OHNE Externalisierungs-Kandidat/Blob, origin bleibt.
    [Fact]
    public void CleanPage_EvictsRefetchableWithoutExternalizationCandidate()
    {
        var skill = Seg(
            kind: "skill_content",
            refetchable: true,
            origin: "skill://foo",
            tokens: 5000,
            lastReferencedTurn: 0);

        var plan = MinorCollector.PlanFull(new[] { skill }, PolicyConfig.Default(), currentTurn: 20);

        Assert.Equal(SegmentState.Evicted, skill.State);
        Assert.Empty(plan.ExternalizationCandidates);
        Assert.Empty(plan.UnitEvicted);
        Assert.Contains(skill, plan.CleanPageEvicted);
        // origin bleibt im Audit-Trail erhalten (Spec §3.2.1).
        Assert.Equal("skill://foo", skill.Origin);
    }

    // AC4: TTL-Eviction respektiert pinned und Static — beide werden NIE evicted.
    [Fact]
    public void TtlEviction_NeverEvictsPinnedOrStatic()
    {
        var pinned = Seg(kind: "tool_result", pinned: true, lastReferencedTurn: 0);
        var stat = Seg(kind: "tool_result", region: Region.Static, lastReferencedTurn: 0);

        var plan = MinorCollector.PlanFull(new[] { pinned, stat }, PolicyConfig.Default(), currentTurn: 50);

        Assert.Equal(SegmentState.Live, pinned.State);
        Assert.Equal(SegmentState.Live, stat.State);
        Assert.Empty(plan.CleanPageEvicted);
        Assert.Empty(plan.UnitEvicted);
        Assert.Empty(plan.ExternalizationCandidates);
    }

    // AC5: Unit-Kopplung — Externalisierung des tool_result lässt den tool_call live.
    [Fact]
    public void Externalization_OnlyTargetsToolResult_LeavesToolCallLive()
    {
        var call = Seg(kind: "tool_call", toolCallId: "tc-1", tokens: 5000, lastReferencedTurn: 10);
        var result = Seg(kind: "tool_result", toolCallId: "tc-1", tokens: 5000, lastReferencedTurn: 10);

        var plan = MinorCollector.PlanFull(new[] { call, result }, PolicyConfig.Default(), currentTurn: 10);

        var candidate = Assert.Single(plan.ExternalizationCandidates);
        Assert.Same(result, candidate.Segment);
        // tool_call bleibt live, tool_result wird nur GEPLANT (kein I/O im Collector).
        Assert.Equal(SegmentState.Live, call.State);
        Assert.Equal(SegmentState.Live, result.State);
    }

    // AC5: Unit-Eviction — TTL-Eviction einer Unit entfernt BEIDE Segmente.
    [Fact]
    public void TtlEviction_OnUnit_RemovesBothSegments()
    {
        var call = Seg(kind: "tool_call", toolCallId: "tc-9", tokens: 10, lastReferencedTurn: 0);
        var result = Seg(kind: "tool_result", toolCallId: "tc-9", tokens: 10, lastReferencedTurn: 0);

        var plan = MinorCollector.PlanFull(new[] { call, result }, PolicyConfig.Default(), currentTurn: 20);

        Assert.Equal(SegmentState.Evicted, call.State);
        Assert.Equal(SegmentState.Evicted, result.State);
        // AC5: eine gekoppelte Unit ergibt GENAU eine EvictedUnit über beide Segmente (Spec §6).
        var evictedUnit = Assert.Single(plan.UnitEvicted);
        Assert.Equal("tc-9", evictedUnit.UnitId);
        Assert.Contains(call, evictedUnit.Segments);
        Assert.Contains(result, evictedUnit.Segments);
        Assert.Empty(plan.CleanPageEvicted);
    }

    // AC5: Eine Unit mit pinned-Segment bleibt vollständig erhalten (kein Teil-Evict).
    [Fact]
    public void TtlEviction_OnUnit_SkippedWhenAnySegmentPinned()
    {
        var call = Seg(kind: "tool_call", toolCallId: "tc-7", pinned: true, lastReferencedTurn: 0);
        var result = Seg(kind: "tool_result", toolCallId: "tc-7", lastReferencedTurn: 0);

        var plan = MinorCollector.PlanFull(new[] { call, result }, PolicyConfig.Default(), currentTurn: 20);

        Assert.Equal(SegmentState.Live, call.State);
        Assert.Equal(SegmentState.Live, result.State);
        Assert.Empty(plan.UnitEvicted);
        Assert.Empty(plan.CleanPageEvicted);
    }

    // Emergency: CollectEmergencyNoIo liefert KEINE Externalisierung (Phase b ausgelassen),
    // nur Clean-Page + TTL-Evictions (Spec §3.1).
    [Fact]
    public void Emergency_SkipsExternalization_OnlyEvicts()
    {
        // Externalisierungs-Kandidat (nicht-refetchable, groß, Kind externalize=true), TTL NICHT
        // überschritten (frisch referenziert) — bliebe in PlanFull ein reiner Phase-b-Kandidat.
        var bigResult = Seg(kind: "tool_result", toolCallId: "tc-2", tokens: 5000, lastReferencedTurn: 50);
        var bigCall = Seg(kind: "tool_call", toolCallId: "tc-2", tokens: 10, lastReferencedTurn: 50);
        // Clean-Page-Kandidat (refetchable, TTL überschritten).
        var skill = Seg(kind: "skill_content", refetchable: true, origin: "skill://x", lastReferencedTurn: 0);
        // TTL-Kandidat (nicht gekoppelt, TTL überschritten).
        var oldUser = Seg(kind: "user_msg", lastReferencedTurn: 0);

        var plan = MinorCollector.CollectEmergencyNoIo(
            new[] { bigResult, bigCall, skill, oldUser },
            PolicyConfig.Default(),
            currentTurn: 50);

        // Phase b ausgelassen: das große, nicht abgelaufene tool_result bleibt live.
        Assert.Equal(SegmentState.Live, bigResult.State);
        Assert.Equal(SegmentState.Live, bigCall.State);
        Assert.Empty(plan.ExternalizationCandidates);
        // Clean-Page + TTL evictions wurden ausgeführt.
        Assert.Equal(SegmentState.Evicted, skill.State);
        Assert.Equal(SegmentState.Evicted, oldUser.State);
        // skill ist Clean-Page (refetchable), oldUser ist eine TTL-Unit (single-segment).
        Assert.Contains(skill, plan.CleanPageEvicted);
        var oldUserUnit = Assert.Single(plan.UnitEvicted);
        Assert.Contains(oldUser, oldUserUnit.Segments);
    }
}
