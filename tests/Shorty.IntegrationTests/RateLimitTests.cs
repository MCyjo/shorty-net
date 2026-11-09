using System.Net;
using System.Net.Http.Json;
using Shorty.IntegrationTests.Support;

namespace Shorty.IntegrationTests;

/// <summary>The limits as a client meets them over HTTP. Allowances are made so slow to refill that timing cannot matter.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class RateLimitTests(PostgresFixture postgres)
{
    private static Dictionary<string, string> CreateLimit(int limit, bool trustForwardedHeaders = false) => new()
    {
        ["RateLimiting:Enabled"] = "true",
        ["RateLimiting:TrustForwardedHeaders"] = trustForwardedHeaders ? "true" : "false",
        ["RateLimiting:Create:Algorithm"] = "TokenBucket",
        ["RateLimiting:Create:Limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["RateLimiting:Create:Window"] = "10:00:00", // one token back every few hours: the allowance is all there is
    };

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/links", UriKind.Relative))
        {
            Content = JsonContent.Create(new { url = "https://example.com/limited" }),
        };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task CreatingLinks_StopsAtTheLimit_AnswersWith429AndRetryAfter_AndStoresNothingMore()
    {
        var connectionString = await postgres.CreateDatabaseAsync("limit");
        using var metrics = new MetricsProbe();
        await using var instance = ShortyInstance.Start(connectionString, settings: CreateLimit(5));
        using var client = instance.Client();

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 8; i++) responses.Add(await PostAsync(client));
        try
        {
            Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            var refused = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
            Assert.Equal(3, refused.Count);
            Assert.All(refused, r => Assert.True(int.Parse(r.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) >= 1));
            Assert.Equal(5, metrics.Total("shorty.links.created")); // a refused request must not reach the code that creates links
            Assert.Equal(3, metrics.Total("shorty.ratelimit.rejected"));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task RedirectsAreLimitedBeforeTheCacheIsAsked()
    {
        var connectionString = await postgres.CreateDatabaseAsync("redirectlimit");
        using var metrics = new MetricsProbe();
        await using var instance = ShortyInstance.Start(connectionString, settings: new Dictionary<string, string>
        {
            ["RateLimiting:Enabled"] = "true",
            ["RateLimiting:Redirect:Algorithm"] = "SlidingWindow",
            ["RateLimiting:Redirect:Limit"] = "3",
            ["RateLimiting:Redirect:Window"] = "10:00:00",
        });
        using var client = instance.Client();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var response = await client.GetAsync(new Uri("/scan-me", UriKind.Relative));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.NotFound));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(1, metrics.Total("shorty.cache.misses")); // the refused requests never touched the cache or the database
    }

    [Fact]
    public async Task HealthChecks_AreNeverLimited()
    {
        var connectionString = await postgres.CreateDatabaseAsync("healthlimit");
        await using var instance = ShortyInstance.Start(connectionString, settings: CreateLimit(1));
        using var client = instance.Client();
        (await PostAsync(client)).Dispose();
        (await PostAsync(client)).Dispose(); // the allowance is gone

        for (var i = 0; i < 20; i++)
        {
            using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, live.StatusCode); // an orchestrator probing must not be turned away
        }
    }

    [Fact]
    public async Task ForwardedHeaders_AreIgnoredByDefault_SoAClientCannotPickItsOwnAddress()
    {
        var connectionString = await postgres.CreateDatabaseAsync("spoof");
        await using var instance = ShortyInstance.Start(connectionString, settings: CreateLimit(2));
        using var client = instance.Client();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var response = await PostAsync(client, forwardedFor: $"198.51.100.{i}"); // a new "address" every time
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task BehindATrustedProxy_EachForwardedClientHasItsOwnAllowance()
    {
        var connectionString = await postgres.CreateDatabaseAsync("proxy");
        await using var instance = ShortyInstance.Start(connectionString, settings: CreateLimit(2, trustForwardedHeaders: true));
        using var client = instance.Client();

        var first = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await PostAsync(client, forwardedFor: "203.0.113.10");
            first.Add(response.StatusCode);
        }
        using var other = await PostAsync(client, forwardedFor: "203.0.113.11");

        Assert.Equal(2, first.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(2, first.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(HttpStatusCode.Created, other.StatusCode); // one client using up its allowance did not take another's
    }
}
