using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Persistence;

/// <summary>
/// Persistenz-Roundtrips (Spec §7): Add → SaveChanges → frischer Context/Query liefert die
/// Entität verlustfrei zurück. Enum-Spalten müssen als snake_case-Strings gespeichert sein
/// (Wire-Format, CLAUDE.md). Eine DB pro Test über die Factory.
/// </summary>
public sealed class DbContextRoundtripTests
{
    private const string Tenant = "t1";

    private static Session NewSession(Ulid id) => new(
        id: id,
        tenantId: Tenant,
        agentTemplateId: "tmpl-1",
        policy: PolicyConfig.Default(),
        contextVersion: 7,
        staticEpoch: 2,
        currentTurn: 3,
        status: SessionStatus.Active,
        createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        updatedAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

    [Fact] // Spec §7 / §2.1: Session inkl. PolicyConfig-Snapshot roundtrippt.
    public void Session_WithPolicySnapshot_Roundtrips()
    {
        using var factory = new SqliteDbContextFactory();
        var id = Ulid.NewUlid();

        using (var write = factory.CreateContext(Tenant))
        {
            write.Sessions.Add(NewSession(id));
            write.SaveChanges();
        }

        using var read = factory.CreateContext(Tenant);
        var loaded = read.Sessions.Single(s => s.Id == id);

        Assert.Equal(Tenant, loaded.TenantId);
        Assert.Equal("tmpl-1", loaded.AgentTemplateId);
        Assert.Equal(7, loaded.ContextVersion);
        Assert.Equal(2, loaded.StaticEpoch);
        Assert.Equal(3, loaded.CurrentTurn);
        Assert.Equal(SessionStatus.Active, loaded.Status);

        // PolicyConfig-Snapshot (Spec §5): Watermarks-Defaults + externalize_threshold + kinds.
        Assert.Equal(180_000, loaded.Policy.BudgetTokens);
        Assert.Equal(0.60, loaded.Policy.Watermarks.Soft);
        Assert.Equal(0.80, loaded.Policy.Watermarks.Hard);
        Assert.Equal(0.95, loaded.Policy.Watermarks.Emergency);
        Assert.Equal(2000, loaded.Policy.ExternalizeThresholdTokens);
        Assert.True(loaded.Policy.Kinds.ContainsKey("tool_result"));
        Assert.True(loaded.Policy.Kinds["tool_result"].Externalize);
        Assert.Null(loaded.Policy.Kinds["decision"].TtlTurns); // ∞
    }

    [Fact] // Spec §7 / §2.5: Session mit Frames (Navigation über Backing-Field) roundtrippt.
    public void Session_WithFrames_Roundtrips()
    {
        using var factory = new SqliteDbContextFactory();
        var sessionId = Ulid.NewUlid();
        var frameId = Ulid.NewUlid();

        using (var write = factory.CreateContext(Tenant))
        {
            var session = NewSession(sessionId);
            session.AddFrame(new Frame(
                id: frameId,
                sessionId: sessionId,
                tenantId: Tenant,
                parentFrameId: null,
                label: "research_subtask",
                openedTurn: 1,
                status: FrameStatus.Open));
            write.Sessions.Add(session);
            write.SaveChanges();
        }

        using var read = factory.CreateContext(Tenant);
        var loaded = read.Sessions
            .Include(s => s.Frames)
            .Single(s => s.Id == sessionId);

        var frame = Assert.Single(loaded.Frames);
        Assert.Equal(frameId, frame.Id);
        Assert.Equal("research_subtask", frame.Label);
        Assert.Equal(FrameStatus.Open, frame.Status);
    }

    [Fact] // Spec §7 / §2.2: live-Segment roundtrippt.
    public void Segment_Live_Roundtrips()
    {
        using var factory = new SqliteDbContextFactory();
        var id = Ulid.NewUlid();
        var sessionId = Ulid.NewUlid();

        using (var write = factory.CreateContext(Tenant))
        {
            write.Segments.Add(Segment.CreateLive(
                id: id,
                sessionId: sessionId,
                tenantId: Tenant,
                region: Region.Working,
                kind: "user_msg",
                role: Role.User,
                content: "hi there",
                seq: 5,
                createdTurn: 1,
                tokens: 3));
            write.SaveChanges();
        }

        using var read = factory.CreateContext(Tenant);
        var loaded = read.Segments.Single(s => s.Id == id);

        Assert.Equal(sessionId, loaded.SessionId);
        Assert.Equal(Region.Working, loaded.Region);
        Assert.Equal(Role.User, loaded.Role);
        Assert.Equal("hi there", loaded.Content);
        Assert.Null(loaded.BlobRef);
        Assert.Equal(SegmentState.Live, loaded.State);
        Assert.Equal(5, loaded.Seq);
        Assert.Equal(3, loaded.Tokens);
    }

    [Fact] // Spec §7 / §2.2 / §2.6: externalisiertes Segment inkl. BlobRef-JSON roundtrippt.
    public void Segment_Externalized_WithBlobRef_Roundtrips()
    {
        using var factory = new SqliteDbContextFactory();
        var id = Ulid.NewUlid();
        var blob = new BlobRef("local", "sha256:deadbeef", 4096, "application/json");

        using (var write = factory.CreateContext(Tenant))
        {
            write.Segments.Add(Segment.CreateExternalized(
                id: id,
                sessionId: Ulid.NewUlid(),
                tenantId: Tenant,
                region: Region.Working,
                kind: "tool_result",
                role: Role.Tool,
                blobRef: blob,
                summary: "JSON tool output (4 KiB)",
                seq: 9,
                createdTurn: 2));
            write.SaveChanges();
        }

        using var read = factory.CreateContext(Tenant);
        var loaded = read.Segments.Single(s => s.Id == id);

        Assert.Null(loaded.Content);
        Assert.Equal(SegmentState.Externalized, loaded.State);
        Assert.NotNull(loaded.BlobRef);
        Assert.Equal("local", loaded.BlobRef!.Store);
        Assert.Equal("sha256:deadbeef", loaded.BlobRef.Key);
        Assert.Equal(4096, loaded.BlobRef.SizeBytes);
        Assert.Equal("application/json", loaded.BlobRef.ContentType);
        Assert.Equal("JSON tool output (4 KiB)", loaded.Summary);
    }

    [Fact] // Spec §7 / §2.5: Frame als eigenständige Entität roundtrippt.
    public void Frame_Roundtrips()
    {
        using var factory = new SqliteDbContextFactory();
        var id = Ulid.NewUlid();
        var sessionId = Ulid.NewUlid();
        var parentId = Ulid.NewUlid();

        using (var write = factory.CreateContext(Tenant))
        {
            // Frame referenziert die Session über die FK SessionId (Spec §2.5) — Session zuerst anlegen.
            write.Sessions.Add(NewSession(sessionId));
            write.Frames.Add(new Frame(
                id: id,
                sessionId: sessionId,
                tenantId: Tenant,
                parentFrameId: parentId,
                label: "child",
                openedTurn: 4,
                status: FrameStatus.Popped));
            write.SaveChanges();
        }

        using var read = factory.CreateContext(Tenant);
        var loaded = read.Frames.Single(f => f.Id == id);

        Assert.Equal(sessionId, loaded.SessionId);
        Assert.Equal(parentId, loaded.ParentFrameId);
        Assert.Equal("child", loaded.Label);
        Assert.Equal(4, loaded.OpenedTurn);
        Assert.Equal(FrameStatus.Popped, loaded.Status);
    }

    [Fact] // Spec §7 / §6: EventRecord (Outbox) roundtrippt.
    public void EventRecord_Roundtrips()
    {
        using var factory = new SqliteDbContextFactory();
        var id = Ulid.NewUlid();
        var sessionId = Ulid.NewUlid();

        using (var write = factory.CreateContext(Tenant))
        {
            write.Events.Add(new EventRecord
            {
                Id = id,
                TenantId = Tenant,
                SessionId = sessionId,
                Type = "segment_appended",
                Payload = "{\"seq\":1}",
                Seq = 1,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
            write.SaveChanges();
        }

        using var read = factory.CreateContext(Tenant);
        var loaded = read.Events.Single(e => e.Id == id);

        Assert.Equal(sessionId, loaded.SessionId);
        Assert.Equal("segment_appended", loaded.Type);
        Assert.Equal("{\"seq\":1}", loaded.Payload);
        Assert.Equal(1, loaded.Seq);
    }

    [Fact] // Spec §7 / §4.4: IdempotencyKeyRecord mit zusammengesetztem Schlüssel roundtrippt.
    public void IdempotencyKeyRecord_CompositeKey_Roundtrips()
    {
        using var factory = new SqliteDbContextFactory();
        var sessionId = Ulid.NewUlid();

        using (var write = factory.CreateContext(Tenant))
        {
            write.IdempotencyKeys.Add(new IdempotencyKeyRecord
            {
                Key = "idem-123",
                TenantId = Tenant,
                SessionId = sessionId,
                ResponseStatusCode = 201,
                ResponseBody = "{\"ok\":true}",
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            });
            write.SaveChanges();
        }

        using var read = factory.CreateContext(Tenant);
        var loaded = read.IdempotencyKeys.Single(k => k.Key == "idem-123");

        Assert.Equal(Tenant, loaded.TenantId);
        Assert.Equal(sessionId, loaded.SessionId);
        Assert.Equal(201, loaded.ResponseStatusCode);
        Assert.Equal("{\"ok\":true}", loaded.ResponseBody);
    }

    [Fact] // Wire-Format (CLAUDE.md): Enum-Spalten sind snake_case-Strings in der DB.
    public void EnumColumns_PersistAsSnakeCaseStrings()
    {
        using var factory = new SqliteDbContextFactory();
        var segmentId = Ulid.NewUlid();
        var sessionId = Ulid.NewUlid();
        var frameId = Ulid.NewUlid();

        using (var write = factory.CreateContext(Tenant))
        {
            write.Sessions.Add(NewSession(sessionId));
            write.Frames.Add(new Frame(
                id: frameId,
                sessionId: sessionId,
                tenantId: Tenant,
                parentFrameId: null,
                label: "f",
                openedTurn: 0,
                status: FrameStatus.Open));
            write.Segments.Add(Segment.CreateLive(
                id: segmentId,
                sessionId: sessionId,
                tenantId: Tenant,
                region: Region.Static,
                kind: "system_prompt",
                role: Role.System,
                content: "sys",
                seq: 1,
                createdTurn: 0));
            write.SaveChanges();
        }

        // Rohes SQL über die gemeinsame Verbindung: Spalten halten snake_case-Strings.
        Assert.Equal("static", ScalarString(factory.Connection,
            "SELECT region FROM segments WHERE id = $id", segmentId.ToString()));
        Assert.Equal("system", ScalarString(factory.Connection,
            "SELECT role FROM segments WHERE id = $id", segmentId.ToString()));
        Assert.Equal("live", ScalarString(factory.Connection,
            "SELECT state FROM segments WHERE id = $id", segmentId.ToString()));
        Assert.Equal("active", ScalarString(factory.Connection,
            "SELECT status FROM sessions WHERE id = $id", sessionId.ToString()));
        Assert.Equal("open", ScalarString(factory.Connection,
            "SELECT status FROM frames WHERE id = $id", frameId.ToString()));
    }

    private static string? ScalarString(SqliteConnection conn, string sql, string idValue)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", idValue);
        return cmd.ExecuteScalar() as string;
    }
}
