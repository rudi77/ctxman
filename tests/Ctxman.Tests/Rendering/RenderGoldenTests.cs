using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Core.Rendering;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Rendering;

/// <summary>
/// Golden-File-Tests für Render-Determinismus (Spec §4.6, I4; WP3-Akzeptanz 9, 16).
/// </summary>
public sealed class RenderGoldenTests
{
    private static readonly Ulid FixtureSessionId = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV");

    private static Session FixtureSession() => new(
        id: FixtureSessionId,
        tenantId: "golden",
        agentTemplateId: null,
        policy: PolicyConfig.Default(),
        contextVersion: 1,
        staticEpoch: 0,
        currentTurn: 0,
        status: SessionStatus.Active,
        createdAt: DateTimeOffset.UnixEpoch,
        updatedAt: DateTimeOffset.UnixEpoch);

    private static IReadOnlyList<Segment> FixtureSegments() =>
    [
        Segment.CreateLive(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAW"), FixtureSessionId, "golden", Region.Static,
            "system_prompt", Role.System, "You are a test agent.", 0, 0, source: "core", tokens: 5),
        Segment.CreateLive(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAX"), FixtureSessionId, "golden", Region.Static,
            "tool_def", null, """{"name":"git","description":"Git tool"}""", 1, 0, source: "core", tokens: 8),
        Segment.CreateLive(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAY"), FixtureSessionId, "golden", Region.Static,
            "tool_def", null, """{"name":"search","description":"Search"}""", 2, 0, source: "mcp:github", tokens: 8),
        Segment.CreateLive(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAZ"), FixtureSessionId, "golden", Region.Working,
            "user_msg", Role.User, "Hello", 3, 0, tokens: 2),
        Segment.CreateLive(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FB0"), FixtureSessionId, "golden", Region.Working,
            "assistant_msg", Role.Assistant, "Hi there", 4, 0, tokens: 3),
    ];

    private static string GoldenPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Golden", name);

    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai")]
    public void Render_Output_MatchesGoldenFile(string provider)
    {
        var plan = RenderPlanner.Plan(FixtureSession(), FixtureSegments(), PolicyConfig.Default());
        var adapter = provider == "anthropic"
            ? (IProviderAdapter)new AnthropicMessagesAdapter()
            : new OpenAiChatAdapter();

        var rendered = adapter.Render(plan.Model);
        var canonical = CanonicalJson.Serialize(rendered.RequestFragment);
        var goldenFile = GoldenPath($"render-{provider}.json");
        Assert.True(File.Exists(goldenFile), $"Golden file missing: {goldenFile}");

        var expected = File.ReadAllText(goldenFile).TrimEnd('\r', '\n');
        Assert.Equal(expected, canonical);
    }

    [Fact] // Akzeptanz 16 / I4: identischer Segment-Stand ⇒ gleicher cache_prefix_hash.
    public void IdenticalSegmentStand_ProducesSameCachePrefixHash()
    {
        var planA = RenderPlanner.Plan(FixtureSession(), FixtureSegments(), PolicyConfig.Default());
        var planB = RenderPlanner.Plan(FixtureSession(), FixtureSegments().Reverse().ToList(), PolicyConfig.Default());

        var hashA = CanonicalJson.ComputeCachePrefixHash(RenderStaticPrefix.BuildHashInput(planA.StaticPrefix));
        var hashB = CanonicalJson.ComputeCachePrefixHash(RenderStaticPrefix.BuildHashInput(planB.StaticPrefix));

        Assert.Equal(hashA, hashB);
    }

    [Fact] // Akzeptanz 16: Toggle-Off/On ⇒ byte-identischer Static-Prefix, epoch steigt.
    public void ToggleOffOn_RestoresByteIdenticalStaticPrefix()
    {
        var session = FixtureSession();

        var withoutGithub = FixtureSegments()
            .Where(s => s.Region != Region.Static || s.Source != "mcp:github")
            .ToList();

        var hashOriginal = CanonicalJson.ComputeCachePrefixHash(
            RenderStaticPrefix.BuildHashInput(RenderPlanner.Plan(session, FixtureSegments(), PolicyConfig.Default()).StaticPrefix));

        var hashWithout = CanonicalJson.ComputeCachePrefixHash(
            RenderStaticPrefix.BuildHashInput(RenderPlanner.Plan(session, withoutGithub, PolicyConfig.Default()).StaticPrefix));

        Assert.NotEqual(hashOriginal, hashWithout);

        var hashRestored = CanonicalJson.ComputeCachePrefixHash(
            RenderStaticPrefix.BuildHashInput(RenderPlanner.Plan(session, FixtureSegments(), PolicyConfig.Default()).StaticPrefix));

        Assert.Equal(hashOriginal, hashRestored);
    }
}
