namespace Ctxman.Tests.Support;

/// <summary>
/// Test-only <see cref="TimeProvider"/> mit fixierter „now" — erlaubt das deterministische Kreuzen
/// der Blob-Grace-Grenze (Spec §7.1: <c>now − LastModified &gt; blob_grace</c>) ohne <c>Sleep</c>.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
