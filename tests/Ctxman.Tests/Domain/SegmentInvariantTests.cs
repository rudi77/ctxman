using Ctxman.Core.Domain;

namespace Ctxman.Tests.Domain;

/// <summary>
/// Tests der Segment-Invarianten I1–I3 (Spec §2.2). Halten das Spec-Verhalten als Vertrag fest,
/// nicht den zufälligen Code-Pfad.
/// </summary>
public sealed class SegmentInvariantTests
{
    private static Segment StaticSegment() => Segment.CreateLive(
        id: Ulid.NewUlid(),
        sessionId: Ulid.NewUlid(),
        tenantId: "t1",
        region: Region.Static,
        kind: "system_prompt",
        role: Role.System,
        content: "you are an agent",
        seq: 1,
        createdTurn: 0);

    private static Segment WorkingSegment(string content = "tool output") => Segment.CreateLive(
        id: Ulid.NewUlid(),
        sessionId: Ulid.NewUlid(),
        tenantId: "t1",
        region: Region.Working,
        kind: "tool_result",
        role: Role.Tool,
        content: content,
        seq: 2,
        createdTurn: 1);

    private static BlobRef SampleBlob() => new("local", "sha256:abc", 1234, "text/plain");

    // ---- I1: Static-Segmente sind immutable (Spec §2.2 / §4.2) ----

    [Fact] // Spec §2.2 I1
    public void Externalize_OnStaticSegment_Throws()
    {
        var seg = StaticSegment();
        Assert.Throws<StaticRegionImmutableException>(() => seg.Externalize(SampleBlob(), "summary"));
    }

    [Fact] // Spec §2.2 I1
    public void Evict_OnStaticSegment_Throws()
    {
        var seg = StaticSegment();
        Assert.Throws<StaticRegionImmutableException>(() => seg.Evict());
    }

    [Fact] // Spec §2.2 I1
    public void Compact_OnStaticSegment_Throws()
    {
        var seg = StaticSegment();
        Assert.Throws<StaticRegionImmutableException>(() => seg.Compact("short"));
    }

    [Fact] // Spec §2.2 I1
    public void MarkReferenced_OnStaticSegment_Throws()
    {
        var seg = StaticSegment();
        Assert.Throws<StaticRegionImmutableException>(() => seg.MarkReferenced(5));
    }

    [Fact] // Spec §2.2 I1
    public void Pin_OnStaticSegment_Throws()
    {
        var seg = StaticSegment();
        Assert.Throws<StaticRegionImmutableException>(() => seg.Pin());
    }

    [Fact] // Spec §2.2 I1
    public void Unpin_OnStaticSegment_Throws()
    {
        var seg = StaticSegment();
        Assert.Throws<StaticRegionImmutableException>(() => seg.Unpin());
    }

    [Fact] // Spec §2.2 I1: keine der Mutationen verändert ein Static-Segment.
    public void StaticSegment_StateUnchanged_AfterRejectedMutations()
    {
        var seg = StaticSegment();
        Assert.Throws<StaticRegionImmutableException>(() => seg.Evict());
        Assert.Equal(SegmentState.Live, seg.State);
        Assert.Equal("you are an agent", seg.Content);
        Assert.Null(seg.BlobRef);
    }

    // ---- I2: live | externalized nie content==null && blob_ref==null (Spec §2.2) ----

    [Fact] // Spec §2.2 I2
    public void Construct_LiveSegment_WithoutContentAndBlobRef_Throws()
    {
        var ex = Assert.Throws<SegmentContentInvariantException>(() => new Segment(
            id: Ulid.NewUlid(),
            sessionId: Ulid.NewUlid(),
            tenantId: "t1",
            region: Region.Working,
            kind: "user_msg",
            source: null,
            role: Role.User,
            content: null,
            blobRef: null,
            refetchable: false,
            origin: null,
            summary: null,
            toolCallId: null,
            frameId: null,
            pinned: false,
            createdTurn: 0,
            lastReferencedTurn: 0,
            tokens: 0,
            seq: 1,
            state: SegmentState.Live));

        Assert.Equal(SegmentState.Live, ex.State);
    }

    [Fact] // Spec §2.2 I2
    public void Construct_ExternalizedSegment_WithoutContentAndBlobRef_Throws()
    {
        Assert.Throws<SegmentContentInvariantException>(() => new Segment(
            id: Ulid.NewUlid(),
            sessionId: Ulid.NewUlid(),
            tenantId: "t1",
            region: Region.Working,
            kind: "tool_result",
            source: null,
            role: Role.Tool,
            content: null,
            blobRef: null,
            refetchable: false,
            origin: null,
            summary: "summary",
            toolCallId: null,
            frameId: null,
            pinned: false,
            createdTurn: 0,
            lastReferencedTurn: 0,
            tokens: 0,
            seq: 1,
            state: SegmentState.Externalized));
    }

    [Fact] // Spec §2.2 I2: gültige Konstruktion mit content gelingt.
    public void Construct_LiveSegment_WithContent_Succeeds()
    {
        var seg = WorkingSegment("hello");
        Assert.Equal("hello", seg.Content);
        Assert.Null(seg.BlobRef);
        Assert.Equal(SegmentState.Live, seg.State);
    }

    [Fact] // Spec §2.2 I2: gültige Konstruktion mit blob_ref gelingt.
    public void Construct_ExternalizedSegment_WithBlobRef_Succeeds()
    {
        var seg = Segment.CreateExternalized(
            id: Ulid.NewUlid(),
            sessionId: Ulid.NewUlid(),
            tenantId: "t1",
            region: Region.Working,
            kind: "tool_result",
            role: Role.Tool,
            blobRef: SampleBlob(),
            summary: "summary",
            seq: 1,
            createdTurn: 0);

        Assert.Null(seg.Content);
        Assert.NotNull(seg.BlobRef);
        Assert.Equal(SegmentState.Externalized, seg.State);
    }

    [Fact] // Spec §2.2 I2: evicted/compacted dürfen beide null sein (Soft-Delete erhält Metadaten).
    public void Construct_EvictedSegment_WithoutContentAndBlobRef_Succeeds()
    {
        var seg = new Segment(
            id: Ulid.NewUlid(),
            sessionId: Ulid.NewUlid(),
            tenantId: "t1",
            region: Region.Working,
            kind: "tool_result",
            source: null,
            role: Role.Tool,
            content: null,
            blobRef: null,
            refetchable: false,
            origin: null,
            summary: "gone",
            toolCallId: null,
            frameId: null,
            pinned: false,
            createdTurn: 0,
            lastReferencedTurn: 0,
            tokens: 0,
            seq: 1,
            state: SegmentState.Evicted);

        Assert.Equal(SegmentState.Evicted, seg.State);
    }

    // ---- I3: evicted/compacted sind Soft-Deletes (Spec §2.2 / §3.2 / §3.3) ----

    [Fact] // Spec §2.2 I3
    public void Evict_OnWorkingSegment_SetsEvicted_PreservesContent()
    {
        var seg = WorkingSegment("preserve me");
        seg.Evict();

        Assert.Equal(SegmentState.Evicted, seg.State);
        // Soft-Delete: Daten bleiben für den Audit-Trail erhalten.
        Assert.Equal("preserve me", seg.Content);
    }

    [Fact] // Spec §2.2 I3
    public void Compact_OnWorkingSegment_SetsCompacted_PreservesContent_UpdatesSummary()
    {
        var seg = WorkingSegment("original body");
        seg.Compact("short summary");

        Assert.Equal(SegmentState.Compacted, seg.State);
        // content bleibt als Audit-Metadatum erhalten (Soft-Delete).
        Assert.Equal("original body", seg.Content);
        Assert.Equal("short summary", seg.Summary);
    }

    [Fact] // Spec §2.2 I3: Externalized-Daten bleiben bei Eviction erhalten (blob_ref + summary).
    public void Evict_OnExternalizedSegment_PreservesBlobRefAndSummary()
    {
        var seg = Segment.CreateExternalized(
            id: Ulid.NewUlid(),
            sessionId: Ulid.NewUlid(),
            tenantId: "t1",
            region: Region.Working,
            kind: "tool_result",
            role: Role.Tool,
            blobRef: SampleBlob(),
            summary: "keep",
            seq: 1,
            createdTurn: 0);

        seg.Evict();

        Assert.Equal(SegmentState.Evicted, seg.State);
        Assert.NotNull(seg.BlobRef);
        Assert.Equal("keep", seg.Summary);
    }
}
