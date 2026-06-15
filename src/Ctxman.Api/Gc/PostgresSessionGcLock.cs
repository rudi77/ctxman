using Npgsql;

namespace Ctxman.Api.Gc;

/// <summary>
/// Verteilte <see cref="ISessionGcLock"/>-Implementierung auf Basis von Postgres
/// <c>pg_advisory_lock(bigint)</c> (Spec §8). Anders als der prozesslokale Lock serialisiert sie
/// GC-Läufe einer Session auch über mehrere stateless Replikas hinweg — der Hosted-Worker läuft auf
/// jedem Pod, ohne diesen Lock liefen zwei Pods dieselbe Collection parallel.
///
/// Jeder Acquire öffnet eine DEDIZIERTE Verbindung (getrennt vom scoped DbContext, der innerhalb
/// des Locks seine eigene Transaktion fährt) und hält den Session-Level-Advisory-Lock, bis das
/// Handle disposed wird (<c>pg_advisory_unlock</c> + Verbindung schließen).
/// </summary>
public sealed class PostgresSessionGcLock : ISessionGcLock
{
    private readonly string _connectionString;

    public PostgresSessionGcLock(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IAsyncDisposable> AcquireAsync(string tenantId, Ulid sessionId, CancellationToken ct)
    {
        var key = SessionLockKey.Compute(tenantId, sessionId);

        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);

            // pg_advisory_lock blockiert, bis der Lock frei ist (Session-Level, spannt den ganzen Job).
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection);
            cmd.Parameters.AddWithValue("key", key);
            await cmd.ExecuteNonQueryAsync(ct);

            return new Releaser(connection, key);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly long _key;

        public Releaser(NpgsqlConnection connection, long key)
        {
            _connection = connection;
            _key = key;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", _connection);
                cmd.Parameters.AddWithValue("key", _key);
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                // Das Schließen der Verbindung gibt einen etwaig noch gehaltenen Advisory-Lock
                // ohnehin frei (Session-Ende) — defensiv auch bei Unlock-Fehler.
                await _connection.DisposeAsync();
            }
        }
    }
}
