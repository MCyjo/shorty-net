using System.Reflection;
using Npgsql;

namespace Shorty.Api.Persistence;

/// <summary>
/// Applies the embedded SQL scripts in name order, once each. Several instances starting together are serialised by a
/// PostgreSQL advisory lock, so a fresh database is migrated by exactly one of them while the others wait and then find
/// nothing left to do.
/// </summary>
internal static class MigrationRunner
{
    private const long LockKey = 727_274;

    public static async Task<IReadOnlyList<string>> ApplyAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await ExecuteAsync(connection, $"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            await ExecuteAsync(connection,
                "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", ct);

            var applied = new HashSet<string>(StringComparer.Ordinal);
            await using (var read = new NpgsqlCommand("SELECT version FROM schema_migrations", connection))
            await using (var reader = await read.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) applied.Add(reader.GetString(0));
            }

            var newlyApplied = new List<string>();
            foreach (var (version, sql) in Scripts())
            {
                if (applied.Contains(version)) continue;

                await using var transaction = await connection.BeginTransactionAsync(ct);
                await using (var script = new NpgsqlCommand(sql, connection, transaction))
                    await script.ExecuteNonQueryAsync(ct);
                await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations (version) VALUES ($1)", connection, transaction))
                {
                    record.Parameters.AddWithValue(version);
                    await record.ExecuteNonQueryAsync(ct);
                }
                await transaction.CommitAsync(ct);
                newlyApplied.Add(version);
            }
            return newlyApplied;
        }
        finally
        {
            await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }
    }

    private static IEnumerable<(string Version, string Sql)> Scripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string prefix = "Shorty.Api.Persistence.Migrations.";

        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var text = new StreamReader(stream);
            yield return (resource[prefix.Length..], text.ReadToEnd());
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
