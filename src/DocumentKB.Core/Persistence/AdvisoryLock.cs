using MySqlConnector;

namespace DocumentKB.Core.Persistence;

public sealed class AdvisoryLock : IAsyncDisposable
{
    private readonly MySqlConnection _conn;
    private readonly string _name;
    private bool _held;

    private AdvisoryLock(MySqlConnection conn, string name, bool held)
    {
        _conn = conn;
        _name = name;
        _held = held;
    }

    public static async Task<AdvisoryLock?> TryAcquireAsync(
        string connectionString, string name, CancellationToken ct = default)
    {
        var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand("SELECT GET_LOCK(@n, 0)", conn);
        cmd.Parameters.AddWithValue("@n", name);
        var got = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (got != 1)
        {
            await conn.DisposeAsync();
            return null;
        }
        return new AdvisoryLock(conn, name, held: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_held)
        {
            await using var cmd = new MySqlCommand("SELECT RELEASE_LOCK(@n)", _conn);
            cmd.Parameters.AddWithValue("@n", _name);
            try { await cmd.ExecuteScalarAsync(); } catch { /* connection may be dead */ }
        }
        await _conn.DisposeAsync();
    }
}
