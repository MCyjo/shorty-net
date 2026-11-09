using Npgsql;
using NpgsqlTypes;
using Shorty.Core;

namespace Shorty.Api.Persistence;

internal sealed record LinkRecord(long Id, string Code, string Url, DateTimeOffset? ExpiresAt);

internal enum InsertResult { Inserted, CodeTaken }

internal interface ILinkStore
{
    Task<InsertResult> InsertAsync(LinkRecord link, bool isCustom, CancellationToken ct);
    Task<LinkRecord?> FindAsync(string code, CancellationToken ct);
}

/// <summary>
/// Reserves a block of ids with one atomic UPDATE. Concurrent instances queue on the row lock for the few
/// milliseconds that takes, and each gets a different block; between reservations no instance talks to the database
/// to obtain an id.
/// </summary>
internal sealed class PostgresIdBlockStore(NpgsqlDataSource dataSource) : IIdBlockStore
{
    public async Task<long> ReserveBlockAsync(int size, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE id_counter SET next_id = next_id + $1 WHERE name = 'links' RETURNING next_id - $1");
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = size });

        var start = await command.ExecuteScalarAsync(ct);
        return start is long value
            ? value
            : throw new InvalidOperationException("The id counter row is missing; was the migration applied?");
    }
}

internal sealed class PostgresLinkStore(NpgsqlDataSource dataSource) : ILinkStore
{
    private const string CodeIndex = "ix_links_code";

    public async Task<InsertResult> InsertAsync(LinkRecord link, bool isCustom, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO links (id, code, url, is_custom, expires_at) VALUES ($1, $2, $3, $4, $5)");
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = link.Id });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = link.Code });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = link.Url });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = isCustom });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = link.ExpiresAt.HasValue ? link.ExpiresAt.Value.ToUniversalTime() : DBNull.Value
        });

        try
        {
            await command.ExecuteNonQueryAsync(ct);
            return InsertResult.Inserted;
        }
        // Only the code index means "that code is taken". Any other unique violation (the primary key) is a bug and must surface.
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == CodeIndex)
        {
            return InsertResult.CodeTaken;
        }
    }

    public async Task<LinkRecord?> FindAsync(string code, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT id, url, expires_at FROM links WHERE code = $1");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = code });

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new LinkRecord(
            reader.GetInt64(0),
            code,
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }
}
