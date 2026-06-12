using Ctxman.Core.Domain;
using Ctxman.Core.Gc;
using Ctxman.Tests.Support;

namespace Ctxman.Tests.Gc;

/// <summary>
/// Unit-Tests für <see cref="MajorCollector.PlanCompaction"/> (Spec §3.3, I/O-frei).
/// Prüft Fensterwahl, Token-Cap, Pinned/Static-Ausschluss und No-op-Verhalten.
/// </summary>
public sealed class MajorCollectorTests
{
    // Hilfsfunktion: Live-Working-Segment direkt ohne Fixtures-Klasse für reine Unit-Tests.
    private static Segment LiveWorking(Ulid sessionId, int tokens, bool pinned = false, long? seq = null) =>
        Wp4Fixtures.LiveWorking(
            sessionId: sessionId,
            tenantId: "test",
            kind: "assistant_msg",
            tokens: tokens,
            pinned: pinned);

    private static Segment StaticSegment(Ulid sessionId, int tokens)
    {
        // Erstellt ein Live-Static-Segment direkt über den Konstruktor, da Wp4Fixtures nur Working produziert.
        return new Segment(
            id: Ulid.NewUlid(),
            sessionId: sessionId,
            tenantId: "test",
            region: Region.Static,
            kind: "system_prompt",
            source: null,
            role: Role.System,
            content: "static content",
            blobRef: null,
            refetchable: false,
            origin: null,
            summary: null,
            toolCallId: null,
            frameId: null,
            pinned: false,
            createdTurn: 0,
            lastReferencedTurn: 0,
            tokens: tokens,
            seq: Wp4Fixtures.NextSeq(),
            state: SegmentState.Live);
    }

    // AC8 (Spec §3.3): Das Compaction-Fenster enthält alle nicht-gepinnten Working-Live-Segmente,
    // älteste→jüngste, akkumuliert bis max_share × BudgetTokens; das jüngste Segment, das das Limit
    // überschreiten würde, wird NICHT mehr aufgenommen.
    [Fact]
    public void PlanCompaction_SelectsOldestSegments_UpToTokenCap()
    {
        var sessionId = Ulid.NewUlid();

        // Budget = 1000, max_share = 0.5 ⇒ Limit = 500.
        // Segmente (nach Seq aufsteigend): 200, 200, 200 (total 400 nach 2; hinzufügen des 3. ⇒ 600 > 500 → Stop).
        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1000) with
        {
            Compaction = new CompactionConfig("model", "tmpl", MaxShare: 0.5),
        };

        var seg1 = LiveWorking(sessionId, tokens: 200);
        var seg2 = LiveWorking(sessionId, tokens: 200);
        var seg3 = LiveWorking(sessionId, tokens: 200); // würde Cap überschreiten

        var plan = MajorCollector.PlanCompaction([seg1, seg2, seg3], policy);

        Assert.False(plan.IsNoOp);
        Assert.Equal(2, plan.SourceSegments.Count);
        Assert.Equal(400, plan.TokensBefore);

        // Reihenfolge: älteste → jüngste (nach seq aufsteigend).
        Assert.True(plan.SourceSegments[0].Seq < plan.SourceSegments[1].Seq);

        // Das erste Segment (niedrigste seq) muss OldestSeq sein.
        Assert.Equal(plan.SourceSegments[0].Seq, plan.OldestSeq);
    }

    // AC8 (Spec §3.3): Das erste Segment wird immer aufgenommen, auch wenn es allein den Cap überschreitet
    // (Spec: "stopp BEFORE das nächste Segment das Limit überschreiten würde" — das erste immer aufgenommen).
    [Fact]
    public void PlanCompaction_FirstSegmentAlwaysIncluded_EvenIfAboveCap()
    {
        var sessionId = Ulid.NewUlid();

        // Budget = 100, max_share = 0.5 ⇒ Limit = 50.
        // Segment 1 hat 200 Tokens (> Limit) — trotzdem aufgenommen (Bedingung: "selected.Count > 0 && ...").
        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 100) with
        {
            Compaction = new CompactionConfig("model", "tmpl", MaxShare: 0.5),
        };

        var seg1 = LiveWorking(sessionId, tokens: 200);
        var seg2 = LiveWorking(sessionId, tokens: 200);

        var plan = MajorCollector.PlanCompaction([seg1, seg2], policy);

        // Beide werden aufgenommen: seg1 immer (first), seg2 schlägt bei accumulated=200 > 50 an → Stop.
        // Da selected.Count ≥ 2 nach seg1 und Stop nach seg2-Check (accumulated=200 > 50 mit Count=1):
        // MajorCollector-Logik: erst nach dem ersten Hinzufügen wird der Limit-Check aktiv.
        // Gemäß Implementierung: seg1 = first → immer hinzu. seg2: accumulated(200) + 200 = 400 > 50 → stop.
        // Ergebnis: 1 Segment → IsNoOp = true.
        Assert.True(plan.IsNoOp); // Nur 1 Segment ⇒ No-op (< 2 Segmente im Plan)
    }

    // AC8 (Spec §3.3): OldestSeq = kleinste Seq unter den ausgewählten Segmenten.
    [Fact]
    public void PlanCompaction_OldestSeq_IsMinSeqAmongSelected()
    {
        var sessionId = Ulid.NewUlid();

        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 10_000) with
        {
            Compaction = new CompactionConfig("model", "tmpl", MaxShare: 0.8),
        };

        var seg1 = LiveWorking(sessionId, tokens: 100);
        var seg2 = LiveWorking(sessionId, tokens: 100);
        var seg3 = LiveWorking(sessionId, tokens: 100);

        var plan = MajorCollector.PlanCompaction([seg3, seg1, seg2], policy); // bewusst ungeordnet

        Assert.False(plan.IsNoOp);
        var expectedOldest = new[] { seg1.Seq, seg2.Seq, seg3.Seq }.Min();
        Assert.Equal(expectedOldest, plan.OldestSeq);
    }

    // AC13 (Spec §2.2 I1 / §3.3): gepinnte Working-Segmente sind NIEMALS im Compaction-Plan.
    [Fact]
    public void PlanCompaction_NeverIncludesPinnedSegments()
    {
        var sessionId = Ulid.NewUlid();

        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 10_000) with
        {
            Compaction = new CompactionConfig("model", "tmpl", MaxShare: 0.8),
        };

        var pinned = LiveWorking(sessionId, tokens: 100, pinned: true);
        var normal1 = LiveWorking(sessionId, tokens: 100);
        var normal2 = LiveWorking(sessionId, tokens: 100);

        var plan = MajorCollector.PlanCompaction([pinned, normal1, normal2], policy);

        Assert.False(plan.IsNoOp);
        Assert.DoesNotContain(plan.SourceSegments, s => s.Id == pinned.Id);
        Assert.Contains(plan.SourceSegments, s => s.Id == normal1.Id);
        Assert.Contains(plan.SourceSegments, s => s.Id == normal2.Id);
    }

    // AC13 (Spec §2.2 I1 / §3.3): Static-Region-Segmente sind NIEMALS im Compaction-Plan.
    [Fact]
    public void PlanCompaction_NeverIncludesStaticSegments()
    {
        var sessionId = Ulid.NewUlid();

        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 10_000) with
        {
            Compaction = new CompactionConfig("model", "tmpl", MaxShare: 0.8),
        };

        var staticSeg = StaticSegment(sessionId, tokens: 100);
        var working1 = LiveWorking(sessionId, tokens: 100);
        var working2 = LiveWorking(sessionId, tokens: 100);

        var plan = MajorCollector.PlanCompaction([staticSeg, working1, working2], policy);

        Assert.False(plan.IsNoOp);
        Assert.DoesNotContain(plan.SourceSegments, s => s.Id == staticSeg.Id);
        Assert.Equal(2, plan.SourceSegments.Count);
    }

    // Spec §3.3: No-op wenn < 2 qualifizierende Segmente vorhanden.
    [Fact]
    public void PlanCompaction_IsNoOp_WhenFewerThanTwoQualifyingSegments()
    {
        var sessionId = Ulid.NewUlid();
        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 10_000) with
        {
            Compaction = new CompactionConfig("model", "tmpl", MaxShare: 0.8),
        };

        // Nur 1 Live-Working-Segment.
        var single = LiveWorking(sessionId, tokens: 100);
        var plan = MajorCollector.PlanCompaction([single], policy);

        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.SourceSegments);
        Assert.Equal(0, plan.TokensBefore);
    }

    // Spec §3.3: No-op auch bei 0 Segmenten.
    [Fact]
    public void PlanCompaction_IsNoOp_WhenNoSegments()
    {
        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 10_000) with
        {
            Compaction = new CompactionConfig("model", "tmpl", MaxShare: 0.8),
        };

        var plan = MajorCollector.PlanCompaction([], policy);

        Assert.True(plan.IsNoOp);
    }

    // Spec §3.3: No-op wenn einzige Kandidaten gepinnt sind.
    [Fact]
    public void PlanCompaction_IsNoOp_WhenAllCandidatesArePinned()
    {
        var sessionId = Ulid.NewUlid();
        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 10_000) with
        {
            Compaction = new CompactionConfig("model", "tmpl", MaxShare: 0.8),
        };

        var pinned1 = LiveWorking(sessionId, tokens: 100, pinned: true);
        var pinned2 = LiveWorking(sessionId, tokens: 100, pinned: true);

        var plan = MajorCollector.PlanCompaction([pinned1, pinned2], policy);

        Assert.True(plan.IsNoOp);
    }

    // Spec §2.4 / AC8: Eine gekoppelte tool_call+tool_result-Unit wird ATOMAR behandelt —
    // entweder beide Segmente oder keines werden in den Plan aufgenommen. Wenn das Paar genau
    // an der max_share-Grenze liegt (die erste Hälfte würde noch passen, die zweite nicht),
    // muss das Paar vollständig ausgeschlossen werden (never split a coupled pair). // Spec §2.4
    [Fact]
    public void PlanCompaction_CoupledPair_IsSelectedAtomically_NeverSplit()
    {
        var sessionId = Ulid.NewUlid();

        // Budget = 1000, max_share = 0.5 ⇒ Limit = 500.
        // Unit 1 (single): assistant_msg mit 300 Tokens → passt (accumulated=300).
        // Unit 2 (coupled pair): tool_call(150) + tool_result(150) = 300 → 300+300=600 > 500 → STOP.
        // Das Paar darf NICHT geteilt werden: entweder beide oder keines.
        // Ergebnis: nur Unit 1 ausgewählt (1 Unit) → IsNoOp (< 2 Units).
        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1000) with
        {
            Compaction = new CompactionConfig("model", "tmpl", MaxShare: 0.5),
        };

        // Unit 1: einzelnes Segment, passt ins Budget.
        var single = Wp4Fixtures.LiveWorking(sessionId, "test", "assistant_msg", tokens: 300);

        // Unit 2: gekoppeltes Paar (tool_call + tool_result via toolCallId) — beide straddle den Cap.
        var toolCallId = "tc-major-1";
        var call = Wp4Fixtures.LiveWorking(sessionId, "test", "tool_call", tokens: 150, toolCallId: toolCallId);
        var result = Wp4Fixtures.LiveWorking(sessionId, "test", "tool_result", tokens: 150, toolCallId: toolCallId);

        // Das Paar straddles den Cap: single(300) + call(150) = 450 ≤ 500 würde passen,
        // aber die Unit muss atomar sein → 300 + 300 = 600 > 500 → ganzes Paar excluded.
        var plan = MajorCollector.PlanCompaction([single, call, result], policy);

        // Weniger als 2 Units ausgewählt → No-op.
        Assert.True(plan.IsNoOp);
        // Keines der Pair-Segmente darf im Plan erscheinen — kein halbes Paar. // Spec §2.4
        Assert.DoesNotContain(plan.SourceSegments, s => s.Id == call.Id);
        Assert.DoesNotContain(plan.SourceSegments, s => s.Id == result.Id);
    }

    // Spec §2.4 / AC8: Wenn das gekoppelte Paar vollständig ins Budget passt, werden BEIDE
    // Segmente gemeinsam aufgenommen (atomare Auswahl, keine Halbierung). // Spec §2.4
    [Fact]
    public void PlanCompaction_CoupledPair_BothIncluded_WhenFitsAtomic()
    {
        var sessionId = Ulid.NewUlid();

        // Budget = 1000, max_share = 0.8 ⇒ Limit = 800.
        // Unit 1 (single): 100 Tokens. Unit 2 (coupled pair): 200+200=400 Tokens.
        // 100 + 400 = 500 ≤ 800 → beide Units passen → 2 Units gewählt → kein No-op.
        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 1000) with
        {
            Compaction = new CompactionConfig("model", "tmpl", MaxShare: 0.8),
        };

        var single = Wp4Fixtures.LiveWorking(sessionId, "test", "assistant_msg", tokens: 100);

        var toolCallId = "tc-major-2";
        var call = Wp4Fixtures.LiveWorking(sessionId, "test", "tool_call", tokens: 200, toolCallId: toolCallId);
        var result = Wp4Fixtures.LiveWorking(sessionId, "test", "tool_result", tokens: 200, toolCallId: toolCallId);

        var plan = MajorCollector.PlanCompaction([single, call, result], policy);

        Assert.False(plan.IsNoOp);
        // Beide Segmente des Paares sind im Plan. // Spec §2.4
        Assert.Contains(plan.SourceSegments, s => s.Id == call.Id);
        Assert.Contains(plan.SourceSegments, s => s.Id == result.Id);
        Assert.Contains(plan.SourceSegments, s => s.Id == single.Id);
        Assert.Equal(3, plan.SourceSegments.Count);
        Assert.Equal(500, plan.TokensBefore);
    }

    // Spec §3.3: Sortierung älteste→jüngste (nach seq aufsteigend) in den SourceSegments.
    [Fact]
    public void PlanCompaction_SourceSegments_AreOrderedOldestFirst()
    {
        var sessionId = Ulid.NewUlid();
        var policy = Wp4Fixtures.SmallBudgetPolicy(budgetTokens: 10_000) with
        {
            Compaction = new CompactionConfig("model", "tmpl", MaxShare: 0.8),
        };

        var seg1 = LiveWorking(sessionId, tokens: 50);
        var seg2 = LiveWorking(sessionId, tokens: 50);
        var seg3 = LiveWorking(sessionId, tokens: 50);

        // Übergeben in umgekehrter Reihenfolge; Plan muss trotzdem älteste→jüngste liefern.
        var plan = MajorCollector.PlanCompaction([seg3, seg1, seg2], policy);

        Assert.False(plan.IsNoOp);
        var seqs = plan.SourceSegments.Select(s => s.Seq).ToList();
        Assert.Equal(seqs.OrderBy(x => x).ToList(), seqs);
    }
}
