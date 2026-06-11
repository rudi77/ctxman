using Ctxman.Core.Domain;
using Ctxman.Core.Rendering;

namespace Ctxman.Tests.Rendering;

/// <summary>
/// Tests des Determinismus- und Invarianten-Vertrags des <see cref="RenderPlanner"/>
/// (Spec §2 I3/I4/I5, §2.4, §4.6). Die Tests halten das Spec-Verhalten als Vertrag fest:
/// Insertion-Order-Unabhängigkeit, Static→Working-Ordering, I3-Ausschluss, Externalized-
/// Rendering, Coalescing und I5-Erkennung — nicht den zufälligen Code-Pfad.
/// </summary>
public sealed class RenderPlannerTests
{
    private static readonly Ulid SessionId = Ulid.NewUlid();
    private const string Tenant = "t1";

    private static Session NewSession() => new(
        id: SessionId,
        tenantId: Tenant,
        agentTemplateId: null,
        policy: PolicyConfig.Default(),
        contextVersion: 1,
        staticEpoch: 0,
        currentTurn: 0,
        status: SessionStatus.Active,
        createdAt: DateTimeOffset.UnixEpoch,
        updatedAt: DateTimeOffset.UnixEpoch);

    private static BlobRef SampleBlob() => new("local", "sha256:abc", 1234, "text/plain");

    private static Segment StaticSeg(
        string kind,
        string content,
        string? source = null,
        Role role = Role.System,
        int tokens = 0,
        long seq = 0) => Segment.CreateLive(
        id: Ulid.NewUlid(),
        sessionId: SessionId,
        tenantId: Tenant,
        region: Region.Static,
        kind: kind,
        role: role,
        content: content,
        seq: seq,
        createdTurn: 0,
        source: source,
        tokens: tokens);

    private static Segment WorkingSeg(
        long seq,
        Role role,
        string content,
        string kind = "user_msg",
        string? source = null,
        string? toolCallId = null,
        bool pinned = false,
        int tokens = 0) => Segment.CreateLive(
        id: Ulid.NewUlid(),
        sessionId: SessionId,
        tenantId: Tenant,
        region: Region.Working,
        kind: kind,
        role: role,
        content: content,
        seq: seq,
        createdTurn: 0,
        source: source,
        toolCallId: toolCallId,
        pinned: pinned,
        tokens: tokens);

    private static RenderPlanResult Plan(params Segment[] segments) =>
        RenderPlanner.Plan(NewSession(), segments, PolicyConfig.Default());

    // ---- 1. Static-Sort ist insertion-order-unabhängig (Spec §2.2 I4 / §4.6) ----

    [Fact] // I4: gleiche Static-Segmente in anderer Insertion-Order ⇒ identische kanonische Reihenfolge.
    public void Plan_StaticRegion_IsInsertionOrderIndependent()
    {
        var sysPrompt = StaticSeg(kind: "system_prompt", content: "you are an agent", source: "core");
        var toolGit = StaticSeg(kind: "tool_def", content: "git tool", source: "mcp:github");
        var toolFs = StaticSeg(kind: "tool_def", content: "fs tool", source: "core");

        var orderA = Plan(sysPrompt, toolGit, toolFs);
        var orderB = Plan(toolFs, sysPrompt, toolGit);

        // Kanonische Sortierung nach (source, kind, content_hash) ⇒ identische Reihenfolge.
        var keyA = orderA.StaticPrefix.Select(i => (i.Source, i.Kind, i.ContentHash)).ToList();
        var keyB = orderB.StaticPrefix.Select(i => (i.Source, i.Kind, i.ContentHash)).ToList();
        Assert.Equal(keyA, keyB);
    }

    [Fact] // I4: explizite kanonische Ordnung nach (source, kind, content_hash), ordinal.
    public void Plan_StaticRegion_OrdersBySourceThenKindThenContentHash()
    {
        // core/system_prompt, core/tool_def, mcp:github/tool_def — ordinal "core" < "mcp:github".
        var sysPrompt = StaticSeg(kind: "system_prompt", content: "sys", source: "core");
        var toolCore = StaticSeg(kind: "tool_def", content: "fs", source: "core");
        var toolGit = StaticSeg(kind: "tool_def", content: "git", source: "mcp:github");

        var result = Plan(toolGit, toolCore, sysPrompt);

        var keys = result.StaticPrefix.Select(i => $"{i.Source}/{i.Kind}").ToList();
        Assert.Equal(
            new[]
            {
                "core/system_prompt",
                "core/tool_def",
                "mcp:github/tool_def",
            },
            keys);
    }

    // ---- 2. Working ist seq-sortiert (Spec §2.2 I4 / §4.6) ----

    [Fact] // I4: Working-Segmente kommen strikt nach seq aufsteigend heraus, egal in welcher Order übergeben.
    public void Plan_WorkingRegion_SortsBySeqAscending()
    {
        var s3 = WorkingSeg(seq: 3, role: Role.Assistant, content: "third");
        var s1 = WorkingSeg(seq: 1, role: Role.User, content: "first");
        var s2 = WorkingSeg(seq: 2, role: Role.User, content: "second");

        var result = Plan(s3, s1, s2);

        // Drei verschiedene Rollen-Übergänge (User, User-coalesced, Assistant) ⇒ zwei Messages.
        var texts = result.Model.Messages
            .SelectMany(m => m.Blocks)
            .Select(b => b.Text)
            .ToList();
        Assert.Equal(new[] { "first", "second", "third" }, texts);
    }

    // ---- 3. Pinned bleibt chronologisch (Spec §2.2 I4) ----

    [Fact] // I4: ein gepinntes Working-Segment bleibt an seiner seq-Position, kein separater Block.
    public void Plan_PinnedWorkingSegment_StaysAtChronologicalPosition()
    {
        var s1 = WorkingSeg(seq: 1, role: Role.User, content: "before");
        var pinned = WorkingSeg(seq: 2, role: Role.Assistant, content: "PINNED", pinned: true);
        var s3 = WorkingSeg(seq: 3, role: Role.User, content: "after");

        var result = Plan(s3, pinned, s1);

        var texts = result.Model.Messages
            .SelectMany(m => m.Blocks)
            .Select(b => b.Text)
            .ToList();
        // Pinned erscheint an Position 2 (zwischen before/after), nicht herausgezogen.
        Assert.Equal(new[] { "before", "PINNED", "after" }, texts);
    }

    // ---- 4. I3-Ausschluss (Spec §2.2 I3) ----

    [Fact] // I3: evicted | compacted erscheinen nie; live | externalized erscheinen.
    public void Plan_ExcludesEvictedAndCompacted_IncludesLiveAndExternalized()
    {
        var live = WorkingSeg(seq: 1, role: Role.User, content: "live-content");

        var evicted = WorkingSeg(seq: 2, role: Role.User, content: "evicted-content");
        evicted.Evict();

        var compacted = WorkingSeg(seq: 3, role: Role.User, content: "compacted-content");
        compacted.Compact("compacted-summary");

        var externalized = Segment.CreateExternalized(
            id: Ulid.NewUlid(),
            sessionId: SessionId,
            tenantId: Tenant,
            region: Region.Working,
            kind: "tool_result",
            role: Role.Tool,
            blobRef: SampleBlob(),
            summary: "ext-summary",
            seq: 4,
            createdTurn: 0);

        var result = Plan(live, evicted, compacted, externalized);

        var allText = string.Join("\n", result.Model.Messages
            .SelectMany(m => m.Blocks)
            .Select(b => b.Text));

        Assert.Contains("live-content", allText);
        Assert.Contains("ext-summary", allText); // externalized erscheint als summary
        Assert.DoesNotContain("evicted-content", allText);
        Assert.DoesNotContain("compacted-content", allText);
        Assert.DoesNotContain("compacted-summary", allText);
    }

    // ---- 5. Externalized-Rendering: summary + ref hint, nie Roh-Content (Spec §2.4) ----

    [Fact] // §2.4: externalisiertes Segment ⇒ summary + expand_context_ref-Hinweis; kein Roh-Content.
    public void Plan_ExternalizedSegment_RendersSummaryAndRefHint()
    {
        var externalized = Segment.CreateExternalized(
            id: Ulid.NewUlid(),
            sessionId: SessionId,
            tenantId: Tenant,
            region: Region.Working,
            kind: "tool_result",
            role: Role.Tool,
            blobRef: SampleBlob(),
            summary: "the-summary-text",
            seq: 1,
            createdTurn: 0);

        var result = Plan(externalized);

        var block = Assert.Single(result.Model.Messages.SelectMany(m => m.Blocks));
        Assert.NotNull(block.Text);
        Assert.Contains("the-summary-text", block.Text!);
        Assert.Contains("expand_context_ref", block.Text!);
        Assert.Contains(externalized.Id.ToString(), block.Text!);
        // EmitExpandContextRef wird gesetzt, sobald ein externalisiertes Segment gerendert wird.
        Assert.True(result.Model.EmitExpandContextRef);
    }

    [Fact] // §2.4: kein externalisiertes Segment ⇒ kein expand_context_ref-Hinweis.
    public void Plan_NoExternalized_DoesNotEmitExpandContextRef()
    {
        var live = WorkingSeg(seq: 1, role: Role.User, content: "hello");

        var result = Plan(live);

        Assert.False(result.Model.EmitExpandContextRef);
    }

    // ---- 6. Coalescing benachbarter gleicher Rollen (Spec §4.6, Kriterium 10) ----

    [Fact] // §4.6: zwei aufeinanderfolgende Working-Segmente gleicher Rolle ⇒ eine Message, zwei Blocks.
    public void Plan_AdjacentSameRole_CoalescesIntoOneMessage()
    {
        var a = WorkingSeg(seq: 1, role: Role.User, content: "block-a");
        var b = WorkingSeg(seq: 2, role: Role.User, content: "block-b");

        var result = Plan(a, b);

        var message = Assert.Single(result.Model.Messages);
        Assert.Equal(Role.User, message.Role);
        Assert.Equal(2, message.Blocks.Count);
        Assert.Equal("block-a", message.Blocks[0].Text);
        Assert.Equal("block-b", message.Blocks[1].Text);
    }

    [Fact] // §4.6: zwei aufeinanderfolgende verschiedene Rollen ⇒ zwei getrennte Messages.
    public void Plan_AdjacentDifferentRoles_StayTwoMessages()
    {
        var a = WorkingSeg(seq: 1, role: Role.User, content: "user-msg");
        var b = WorkingSeg(seq: 2, role: Role.Assistant, content: "assistant-msg");

        var result = Plan(a, b);

        Assert.Equal(2, result.Model.Messages.Count);
        Assert.Equal(Role.User, result.Model.Messages[0].Role);
        Assert.Equal(Role.Assistant, result.Model.Messages[1].Role);
        Assert.Equal("user-msg", Assert.Single(result.Model.Messages[0].Blocks).Text);
        Assert.Equal("assistant-msg", Assert.Single(result.Model.Messages[1].Blocks).Text);
    }

    [Fact] // §4.6: gleiche Rolle, aber durch eine andere Rolle getrennt ⇒ nicht coalesced (chronologisch).
    public void Plan_SameRoleSeparatedByOtherRole_DoesNotCoalesce()
    {
        var u1 = WorkingSeg(seq: 1, role: Role.User, content: "u1");
        var a = WorkingSeg(seq: 2, role: Role.Assistant, content: "a");
        var u2 = WorkingSeg(seq: 3, role: Role.User, content: "u2");

        var result = Plan(u1, a, u2);

        Assert.Equal(3, result.Model.Messages.Count);
        Assert.Equal(Role.User, result.Model.Messages[0].Role);
        Assert.Equal(Role.Assistant, result.Model.Messages[1].Role);
        Assert.Equal(Role.User, result.Model.Messages[2].Role);
    }

    // ---- 7. I5-Erkennung: offene Units (Spec §2.2 I5) ----

    [Fact] // I5: tool_call ohne korrespondierendes tool_result ⇒ open_tool_call_ids enthält die id.
    public void Plan_ToolCallWithoutResult_ReportsOpenToolCallId()
    {
        var toolCall = WorkingSeg(
            seq: 1,
            role: Role.Assistant,
            content: "calling tool",
            kind: "tool_call",
            source: "search",
            toolCallId: "call-123");

        var result = Plan(toolCall);

        Assert.Contains("call-123", result.OpenToolCallIds);
    }

    [Fact] // I5: tool_call mit korrespondierendem tool_result ⇒ open_tool_call_ids ist leer.
    public void Plan_ToolCallWithMatchingResult_ReportsNoOpenToolCallIds()
    {
        var toolCall = WorkingSeg(
            seq: 1,
            role: Role.Assistant,
            content: "calling tool",
            kind: "tool_call",
            source: "search",
            toolCallId: "call-123");
        var toolResult = WorkingSeg(
            seq: 2,
            role: Role.Tool,
            content: "result body",
            kind: "tool_result",
            toolCallId: "call-123");

        var result = Plan(toolCall, toolResult);

        Assert.Empty(result.OpenToolCallIds);
    }

    [Fact] // I5: ein erfülltes und ein offenes tool_call ⇒ nur das offene wird gemeldet.
    public void Plan_MixedToolCalls_ReportsOnlyOpenOne()
    {
        var open = WorkingSeg(
            seq: 1, role: Role.Assistant, content: "open call",
            kind: "tool_call", source: "search", toolCallId: "open-1");
        var fulfilledCall = WorkingSeg(
            seq: 2, role: Role.Assistant, content: "fulfilled call",
            kind: "tool_call", source: "search", toolCallId: "done-1");
        var fulfilledResult = WorkingSeg(
            seq: 3, role: Role.Tool, content: "result",
            kind: "tool_result", toolCallId: "done-1");

        var result = Plan(open, fulfilledCall, fulfilledResult);

        Assert.Equal(new[] { "open-1" }, result.OpenToolCallIds);
    }

    // ---- 8. tokens_total = Summe der gerenderten Segment-Tokens (Spec §4.6) ----

    [Fact] // tokens_total = Summe der Tokens der render-eligiblen (live | externalized) Segmente.
    public void Plan_TokensTotal_SumsRenderedSegmentTokens()
    {
        var live1 = WorkingSeg(seq: 1, role: Role.User, content: "a", tokens: 10);
        var live2 = WorkingSeg(seq: 2, role: Role.Assistant, content: "b", tokens: 25);
        var staticSeg = StaticSeg(kind: "system_prompt", content: "sys", source: "core", tokens: 5);

        var result = Plan(live1, live2, staticSeg);

        Assert.Equal(40, result.TokensTotal);
        Assert.Equal(40, result.Model.TokensTotal);
    }

    [Fact] // tokens_total schließt I3-gefilterte (evicted | compacted) Segmente aus.
    public void Plan_TokensTotal_ExcludesEvictedAndCompacted()
    {
        var live = WorkingSeg(seq: 1, role: Role.User, content: "keep", tokens: 7);

        var evicted = WorkingSeg(seq: 2, role: Role.User, content: "gone", tokens: 100);
        evicted.Evict();

        var compacted = WorkingSeg(seq: 3, role: Role.User, content: "shrunk", tokens: 50);
        compacted.Compact("s");

        var result = Plan(live, evicted, compacted);

        Assert.Equal(7, result.TokensTotal);
    }
}
