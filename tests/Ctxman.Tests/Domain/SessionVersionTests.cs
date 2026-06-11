using Ctxman.Core.Domain;

namespace Ctxman.Tests.Domain;

/// <summary>
/// Sicherstellt die context_version-Monotonie (Spec §4.4): IncrementVersion ist der einzige
/// Mutator und erhöht die Version strikt monoton, während UpdatedAt mitgeführt wird.
/// </summary>
public sealed class SessionVersionTests
{
    private static Session NewSession() => new(
        id: Ulid.NewUlid(),
        tenantId: "t1",
        agentTemplateId: "tmpl-1",
        policy: PolicyConfig.Default(),
        contextVersion: 7,
        staticEpoch: 2,
        currentTurn: 3,
        status: SessionStatus.Active,
        createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        updatedAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

    [Fact] // Spec §4.4: context_version steigt strikt monoton, UpdatedAt wird aktualisiert.
    public void Session_IncrementVersion_IsMonotonic()
    {
        var session = NewSession();
        var v0 = session.ContextVersion;

        var t1 = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        session.IncrementVersion(t1);
        var v1 = session.ContextVersion;

        Assert.True(v1 > v0);
        Assert.Equal(t1, session.UpdatedAt);

        var t2 = new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);
        session.IncrementVersion(t2);
        var v2 = session.ContextVersion;

        Assert.True(v2 > v1);
        Assert.Equal(t2, session.UpdatedAt);
    }
}
