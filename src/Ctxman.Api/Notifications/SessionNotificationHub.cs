using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Ctxman.Api.Notifications;

/// <summary>
/// Ein über alle Sessions hinweg push-fähiger Benachrichtigungskanal für Lifecycle-Ereignisse
/// (session_created, session_archived) — die Ergänzung zur per-Session-Event-Outbox (Spec §6),
/// die bewusst session-gescopt ist. Ermöglicht der UI eine Live-Discovery neuer Sessions ohne
/// Polling: <c>POST /v1/sessions</c> publiziert hier, der SSE-Endpunkt
/// <c>GET /v1/sessions/events</c> verteilt an verbundene Clients (tenant-gefiltert dort).
///
/// In-Memory und damit per-Replica: für das lokale Single-Instance-Setup (Dev, eingebettet)
/// vollständig; in einem Multi-Replica-Deployment sieht ein SSE-Client nur die Creates seiner
/// eigenen Replica — die durable Quelle bleibt <c>GET /v1/sessions</c> (Snapshot). Bewusst kein
/// neuer persistenter Store: das ist eine reine Live-Notification, kein Audit-Kanal.
/// </summary>
public sealed class SessionNotificationHub
{
    private readonly ConcurrentDictionary<Guid, Channel<SessionNotification>> _subscribers = new();

    /// <summary>Fan-out an alle aktuell verbundenen Subscriber (non-blocking, DropOldest bei Überlauf).</summary>
    public void Publish(SessionNotification notification)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(notification);
        }
    }

    /// <summary>Registriert einen Subscriber; <see cref="Unsubscribe"/> beim Verbindungsende aufrufen.</summary>
    public SessionSubscription Subscribe()
    {
        // Bounded + DropOldest: ein langsamer/abgehängter Client darf den Publisher nie blockieren.
        var channel = Channel.CreateBounded<SessionNotification>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return new SessionSubscription(id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}

/// <summary>Ein Session-Lifecycle-Ereignis fürs Live-Push (kein Outbox-Event, Spec §6 bleibt session-gescopt).</summary>
public sealed record SessionNotification(
    string Type,        // "session_created" | "session_archived"
    string TenantId,    // für die Tenant-Filterung im SSE-Endpunkt — verlässt den Server nie im Payload
    string SessionId,
    DateTimeOffset At);

public sealed record SessionSubscription(Guid Id, ChannelReader<SessionNotification> Reader);
