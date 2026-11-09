using Npgsql;
using Shorty.Api.Persistence;
using Shorty.Api.RateLimiting;

namespace Shorty.Api.Analytics;

public sealed record DailyClicks(DateOnly Date, long Clicks);

public sealed record LinkStats(string Code, long TotalClicks, int Days, IReadOnlyList<DailyClicks> Daily);

internal static class StatsEndpoint
{
    public static void MapStatsEndpoint(this WebApplication app) =>
        app.MapGet("/api/links/{code}/stats", GetAsync).RequireRateLimiting(RateLimitPolicies.Redirect);

    private static async Task<IResult> GetAsync(
        string code, ILinkStore links, NpgsqlDataSource dataSource, TimeProvider clock, CancellationToken ct, int days = 7)
    {
        if (days is < 1 or > 90) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Must be between 1 and 90."] });
        if (await links.FindAsync(code, ct) is null) return Results.NotFound();

        // Days are UTC days. Clicks arrive through the broker, so the newest few seconds may not be counted yet.
        var since = clock.GetUtcNow().UtcDateTime.Date.AddDays(1 - days);
        await using var command = dataSource.CreateCommand("""
            SELECT (clicked_at AT TIME ZONE 'UTC')::date AS day, count(*) AS clicks
            FROM click_events
            WHERE code = @code AND clicked_at >= @since
            GROUP BY 1 ORDER BY 1
            """);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("since", new DateTimeOffset(since, TimeSpan.Zero));

        var daily = new List<DailyClicks>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                daily.Add(new DailyClicks(DateOnly.FromDateTime(reader.GetDateTime(0)), reader.GetInt64(1)));
        }

        await using var total = dataSource.CreateCommand("SELECT count(*) FROM click_events WHERE code = @code");
        total.Parameters.AddWithValue("code", code);
        var totalClicks = (long)(await total.ExecuteScalarAsync(ct) ?? 0L);

        return Results.Ok(new LinkStats(code, totalClicks, days, daily));
    }
}
