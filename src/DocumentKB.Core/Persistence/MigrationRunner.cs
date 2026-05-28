using System.Reflection;
using MySqlConnector;

namespace DocumentKB.Core.Persistence;

public sealed class MigrationRunner(string connectionString)
{
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using (var ensure = new MySqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_migrations (" +
            "  name VARCHAR(255) PRIMARY KEY," +
            "  applied_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)" +
            ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4", conn))
            await ensure.ExecuteNonQueryAsync(ct);

        var asm = typeof(MigrationRunner).Assembly;
        var resources = asm.GetManifestResourceNames()
            .Where(n => n.Contains(".SqlScripts.") && n.EndsWith(".sql"))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var resName in resources)
        {
            var name = resName[(resName.LastIndexOf('.', resName.Length - 5) + 1)..];
            await using var check = new MySqlCommand(
                "SELECT 1 FROM schema_migrations WHERE name=@n", conn);
            check.Parameters.AddWithValue("@n", name);
            if (await check.ExecuteScalarAsync(ct) is not null) continue;

            await using var stream = asm.GetManifestResourceStream(resName)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);

            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var stmt in SplitStatements(sql))
            {
                await using var cmd = new MySqlCommand(stmt, conn, tx);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var mark = new MySqlCommand(
                "INSERT INTO schema_migrations(name) VALUES(@n)", conn, tx))
            {
                mark.Parameters.AddWithValue("@n", name);
                await mark.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }

    private static IEnumerable<string> SplitStatements(string sql)
        => sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Where(s => s.Length > 0);
}
