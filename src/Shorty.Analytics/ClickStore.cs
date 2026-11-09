using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Shorty.Core.Analytics;

namespace Shorty.Analytics;

/// <summary>Writes clicks to the partitioned <c>click_events</c> table (created by the API's migrations).</summary>
public sealed class ClickStore(NpgsqlDataSource dataSource)
{
    // Distinct from the migration lock, so creating partitions never waits for (or blocks) a migration.
    private const long PartitionLockKey = 7_201_002;

    /// <summary>
    /// Stores the clicks in one statement and returns how many were new. A click that is already there (same id and time)
    /// is skipped: the broker delivers at least once, so the same click can arrive again after a restart or a rebalance.
    /// </summary>
    public async Task<int> InsertAsync(IReadOnlyList<ClickEvent> clicks, CancellationToken ct)
    {
        if (clicks.Count == 0) return 0;

        await using var command = dataSource.CreateCommand("""
            INSERT INTO click_events (event_id, code, clicked_at, referrer_host)
            SELECT * FROM unnest(@ids, @codes, @times, @referrers)
            ON CONFLICT (event_id, clicked_at) DO NOTHING
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = [.. clicks.Select(c => c.EventId)] });
        command.Parameters.Add(new NpgsqlParameter<string[]>("codes", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = [.. clicks.Select(c => c.Code)] });
        command.Parameters.Add(new NpgsqlParameter<DateTime[]>("times", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { TypedValue = [.. clicks.Select(c => c.ClickedAt.UtcDateTime)] });
        command.Parameters.Add(new NpgsqlParameter<string?[]>("referrers", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = [.. clicks.Select(c => c.ReferrerHost)] });
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Creates the monthly partitions from last month to <paramref name="monthsAhead"/> months ahead, if they are not there.
    /// Safe to run from several workers at once.
    /// </summary>
    public async Task EnsurePartitionsAsync(DateTimeOffset now, int monthsAhead, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", PartitionLockKey);
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        var firstOfMonth = new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var offset = -1; offset <= monthsAhead; offset++)
        {
            var from = firstOfMonth.AddMonths(offset);
            var to = from.AddMonths(1);
            // Names and bounds are built from dates, never from input, so formatting them into the statement is safe.
            var sql = string.Create(CultureInfo.InvariantCulture,
                $"CREATE TABLE IF NOT EXISTS click_events_{from:yyyy_MM} PARTITION OF click_events FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}')");
            await using var create = new NpgsqlCommand(sql, connection, transaction);
            await create.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}
