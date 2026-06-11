using Ctxman.Core.Domain;
using Ctxman.Core.Persistence;
using Ctxman.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Ctxman.Tests.Persistence;

/// <summary>
/// Tenant-Isolation auf Query-Ebene (Spec §10 / Akzeptanzkriterium 7): über den regulären
/// Datenzugriffsweg sieht Tenant B keine Daten von Tenant A. Beide Contexts teilen sich
/// dieselbe SQLite-Verbindung (eine DB) — der einzige Unterschied ist der injizierte
/// <c>TenantContext.TenantId</c>, der den Global Query Filter speist.
/// </summary>
public sealed class TenantIsolationTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact] // Spec §10: B sieht A's Daten nicht; Gegenprobe mit IgnoreQueryFilters zeigt sie.
    public void TenantB_DoesNotSee_TenantA_Data()
    {
        using var factory = new SqliteDbContextFactory();
        var sessionId = Ulid.NewUlid();
        var segmentId = Ulid.NewUlid();
        var frameId = Ulid.NewUlid();
        var eventId = Ulid.NewUlid();

        // Tenant A schreibt eine Session, ein Segment, einen Frame und ein Event.
        using (var a = factory.CreateContext(TenantA))
        {
            a.Sessions.Add(new Session(
                id: sessionId,
                tenantId: TenantA,
                agentTemplateId: null,
                policy: PolicyConfig.Default(),
                contextVersion: 0,
                staticEpoch: 0,
                currentTurn: 0,
                status: SessionStatus.Active,
                createdAt: DateTimeOffset.UnixEpoch,
                updatedAt: DateTimeOffset.UnixEpoch));
            a.Segments.Add(Segment.CreateLive(
                id: segmentId,
                sessionId: sessionId,
                tenantId: TenantA,
                region: Region.Working,
                kind: "user_msg",
                role: Role.User,
                content: "secret",
                seq: 1,
                createdTurn: 0));
            a.Frames.Add(new Frame(
                id: frameId,
                sessionId: sessionId,
                tenantId: TenantA,
                parentFrameId: null,
                label: "f",
                openedTurn: 0));
            a.Events.Add(new EventRecord
            {
                Id = eventId,
                TenantId = TenantA,
                SessionId = sessionId,
                Type = "segment_appended",
                Payload = "{}",
                Seq = 1,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
            a.SaveChanges();
        }

        // Tenant B sieht über den regulären Zugriffsweg NICHTS.
        using (var b = factory.CreateContext(TenantB))
        {
            Assert.Empty(b.Sessions.ToList());
            Assert.Empty(b.Segments.ToList());
            Assert.Empty(b.Frames.ToList());
            Assert.Empty(b.Events.ToList());

            // Gegenprobe: ohne Query-Filter sind A's Zeilen physisch in derselben DB vorhanden.
            Assert.Single(b.Sessions.IgnoreQueryFilters().Where(s => s.Id == sessionId).ToList());
            Assert.Single(b.Segments.IgnoreQueryFilters().Where(s => s.Id == segmentId).ToList());
            Assert.Single(b.Frames.IgnoreQueryFilters().Where(f => f.Id == frameId).ToList());
            Assert.Single(b.Events.IgnoreQueryFilters().Where(e => e.Id == eventId).ToList());
        }

        // Tenant A sieht seine eigenen Daten regulär.
        using (var a = factory.CreateContext(TenantA))
        {
            Assert.Single(a.Sessions.ToList());
            Assert.Single(a.Segments.ToList());
            Assert.Single(a.Frames.ToList());
            Assert.Single(a.Events.ToList());
        }
    }
}
