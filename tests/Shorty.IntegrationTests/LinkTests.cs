using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Shorty.Api.Links;
using Shorty.IntegrationTests.Support;

namespace Shorty.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LinkTests(PostgresFixture postgres)
{
    private static async Task<(long Rows, long DistinctCodes)> CountLinksAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*), count(DISTINCT code) FROM links", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, object body) =>
        client.PostAsJsonAsync(new Uri("/api/links", UriKind.Relative), body);

    [Fact]
    public async Task ThreeInstancesStartingAndCreatingLinksTogether_NeverIssueTheSameCode()
    {
        var connectionString = await postgres.CreateDatabaseAsync("many");
        using var metrics = new MetricsProbe();

        // Three application instances start at the same moment against an empty database: they all try to migrate it,
        // and the advisory lock has to make that safe.
        var instances = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => ShortyInstance.Start(connectionString))));
        var clients = instances.Select(i => i.Client()).ToArray();
        try
        {
            const int total = 3_000;
            using var limit = new SemaphoreSlim(64);

            var responses = await Task.WhenAll(Enumerable.Range(0, total).Select(async i =>
            {
                await limit.WaitAsync();
                try
                {
                    using var response = await PostAsync(clients[i % clients.Length], new { url = $"https://example.com/page/{i}" });
                    // Read the body as text first: a failure body is not JSON, and parsing it blindly hides the real error.
                    var text = await response.Content.ReadAsStringAsync();
                    return (response.StatusCode, Text: text);
                }
                finally { limit.Release(); }
            }));

            var failures = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
            Assert.True(failures.Count == 0,
                $"{failures.Count} of {total} requests failed; first: {failures.FirstOrDefault().StatusCode} {failures.FirstOrDefault().Text}");

            var codes = responses.Select(r => System.Text.Json.JsonSerializer.Deserialize<CreateLinkResponse>(r.Text, System.Text.Json.JsonSerializerOptions.Web)!.Code).ToList();
            Assert.Equal(total, codes.Distinct().Count());
            Assert.All(codes, code => Assert.Equal(7, code.Length));

            var (rows, distinct) = await CountLinksAsync(connectionString);
            Assert.Equal(total, rows);
            Assert.Equal(total, distinct);

            // The system's own account of itself: no instance ever had to retry because of an overlapping id.
            Assert.Equal(0, metrics.Total("shorty.code.collisions"));
            Assert.Equal(total, metrics.Total("shorty.links.created"));
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            foreach (var instance in instances) await instance.DisposeAsync();
        }
    }

    [Fact]
    public async Task IdsAreReservedInBlocks_NotOnePerLink()
    {
        var connectionString = await postgres.CreateDatabaseAsync("blocks");
        using var metrics = new MetricsProbe();
        await using var instance = ShortyInstance.Start(connectionString, blockSize: 1000);
        using var client = instance.Client();

        for (var i = 0; i < 2_500; i++)
        {
            using var response = await PostAsync(client, new { url = $"https://example.com/{i}" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        Assert.Equal(3, metrics.Total("shorty.id.blocks_allocated")); // 2,500 links need three blocks of 1,000, not 2,500 round trips
    }

    [Fact]
    public async Task TheSameAliasRequestedInParallel_IsGrantedOnce_AndTheRestGetAConflict()
    {
        var connectionString = await postgres.CreateDatabaseAsync("alias");
        await using var a = ShortyInstance.Start(connectionString);
        await using var b = ShortyInstance.Start(connectionString);
        using var clientA = a.Client();
        using var clientB = b.Client();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
        {
            using var response = await PostAsync(i % 2 == 0 ? clientA : clientB, new { url = $"https://example.com/{i}", alias = "launch-2026" });
            return response.StatusCode;
        }));

        Assert.Single(statuses, s => s == HttpStatusCode.Created);
        Assert.Equal(23, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.DoesNotContain(statuses, s => (int)s >= 500); // the unique index decides; losing the race must not be an error
    }

    [Fact]
    public async Task ACreatedLink_RedirectsWith302ToTheOriginalUrl()
    {
        var connectionString = await postgres.CreateDatabaseAsync("redirect");
        await using var instance = ShortyInstance.Start(connectionString);
        using var client = instance.Client();

        using var created = await PostAsync(client, new { url = "https://example.com/a/very/long/path?x=1" });
        var link = (await created.Content.ReadFromJsonAsync<CreateLinkResponse>())!;
        using var redirect = await client.GetAsync(new Uri($"/{link.Code}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal("https://example.com/a/very/long/path?x=1", redirect.Headers.Location!.OriginalString);
        Assert.EndsWith($"/{link.Code}", link.ShortUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomAlias_WorksAsTheCode()
    {
        var connectionString = await postgres.CreateDatabaseAsync("custom");
        await using var instance = ShortyInstance.Start(connectionString);
        using var client = instance.Client();

        using var created = await PostAsync(client, new { url = "https://example.com/docs", alias = "my-docs" });
        using var redirect = await client.GetAsync(new Uri("/my-docs", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal("https://example.com/docs", redirect.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task UnknownAndExpiredLinks_AreNotRedirected()
    {
        var connectionString = await postgres.CreateDatabaseAsync("expiry");
        await using var instance = ShortyInstance.Start(connectionString);
        using var client = instance.Client();

        using var unknown = await client.GetAsync(new Uri("/nope123", UriKind.Relative));

        // An expiry in the past is refused at creation, so make a link that expires and let it expire.
        using var created = await PostAsync(client, new { url = "https://example.com/soon", alias = "soon-gone", expiresAt = DateTimeOffset.UtcNow.AddSeconds(2) });
        await Task.Delay(TimeSpan.FromSeconds(3));
        using var expired = await client.GetAsync(new Uri("/soon-gone", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("not a url")]
    [InlineData("https://trusted.example@evil.example/")]
    public async Task UnsafeUrls_AreRejectedWith400(string url)
    {
        var connectionString = await postgres.CreateDatabaseAsync("invalid");
        await using var instance = ShortyInstance.Start(connectionString);
        using var client = instance.Client();

        using var response = await PostAsync(client, new { url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((0L, 0L), await CountLinksAsync(connectionString)); // nothing was stored
    }

    [Theory]
    [InlineData("api")]
    [InlineData("health")]
    [InlineData("ab")]
    public async Task ReservedOrMalformedAliases_AreRejectedWith400(string alias)
    {
        var connectionString = await postgres.CreateDatabaseAsync("alias400");
        await using var instance = ShortyInstance.Start(connectionString);
        using var client = instance.Client();

        using var response = await PostAsync(client, new { url = "https://example.com", alias });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
