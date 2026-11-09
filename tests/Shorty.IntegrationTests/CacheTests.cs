using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Shorty.Api.Links;
using Shorty.IntegrationTests.Support;

namespace Shorty.IntegrationTests;

/// <summary>Cache-aside against a real Redis and a real PostgreSQL. "Misses" are lookups that had to read PostgreSQL.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class CacheTests(PostgresFixture postgres, RedisFixture redis)
{
    private static async Task<string> CreateAsync(HttpClient client, string url, string? alias = null, DateTimeOffset? expiresAt = null)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/links", UriKind.Relative), new { url, alias, expiresAt });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateLinkResponse>())!.Code;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string code) =>
        client.GetAsync(new Uri($"/{code}", UriKind.Relative));

    [Fact]
    public async Task RepeatedRedirects_ReadPostgresOnlyOnce()
    {
        await redis.FlushAsync();
        var connectionString = await postgres.CreateDatabaseAsync("hits");
        using var metrics = new MetricsProbe();
        await using var instance = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
        using var client = instance.Client();
        var code = await CreateAsync(client, "https://example.com/hot");

        for (var i = 0; i < 10; i++)
        {
            using var response = await GetAsync(client, code);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        Assert.Equal(10, metrics.Total("shorty.redirects"));
        Assert.Equal(1, metrics.Total("shorty.cache.misses")); // 1 database read, 9 served from the cache
    }

    [Fact]
    public async Task AnotherInstance_FindsTheLinkInRedis_WithoutReadingPostgres()
    {
        await redis.FlushAsync();
        var connectionString = await postgres.CreateDatabaseAsync("shared");
        using var metrics = new MetricsProbe();
        await using var first = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
        await using var second = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
        using var clientA = first.Client();
        using var clientB = second.Client();
        var code = await CreateAsync(clientA, "https://example.com/shared");

        using var viaA = await GetAsync(clientA, code);   // reads PostgreSQL, fills memory and Redis
        using var viaB = await GetAsync(clientB, code);   // empty memory, but Redis has it

        Assert.Equal(HttpStatusCode.Found, viaA.StatusCode);
        Assert.Equal(HttpStatusCode.Found, viaB.StatusCode);
        Assert.Equal(1, metrics.Total("shorty.cache.misses"));
    }

    [Fact]
    public async Task ManyConcurrentRequestsForAnUncachedLink_ShareOneDatabaseRead()
    {
        await redis.FlushAsync();
        var connectionString = await postgres.CreateDatabaseAsync("stampede");
        using var metrics = new MetricsProbe();
        await using var creator = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
        using var creatorClient = creator.Client();
        var code = await CreateAsync(creatorClient, "https://example.com/viral");

        // A fresh instance has nothing in memory and Redis was flushed: the first wave of traffic after a link goes viral.
        await using var instance = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
        using var client = instance.Client();
        var statuses = await Task.WhenAll(Enumerable.Range(0, 300).Select(async _ =>
        {
            using var response = await GetAsync(client, code);
            return response.StatusCode;
        }));

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Found, s));
        Assert.Equal(1, metrics.Total("shorty.cache.misses")); // 300 requests, one query: no stampede on the database
    }

    [Fact]
    public async Task UnknownCodes_AreRemembered_AndWorkAsSoonAsALinkIsCreatedUnderThatName()
    {
        await redis.FlushAsync();
        var connectionString = await postgres.CreateDatabaseAsync("negative");
        using var metrics = new MetricsProbe();
        await using var instance = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
        using var client = instance.Client();

        for (var i = 0; i < 5; i++)
        {
            using var response = await GetAsync(client, "ghost-name");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        var missesWhileUnknown = metrics.Total("shorty.cache.misses");

        await CreateAsync(client, "https://example.com/now-exists", alias: "ghost-name");
        using var afterCreate = await GetAsync(client, "ghost-name");

        Assert.Equal(1, missesWhileUnknown); // someone scanning for codes costs one query per code, not one per request
        Assert.Equal(HttpStatusCode.Found, afterCreate.StatusCode); // the remembered "does not exist" was dropped on creation
    }

    [Fact]
    public async Task AnExpiredLink_IsGoneEvenThoughItIsStillInTheCache()
    {
        await redis.FlushAsync();
        var connectionString = await postgres.CreateDatabaseAsync("expiry");
        using var metrics = new MetricsProbe();
        await using var instance = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
        using var client = instance.Client();
        await CreateAsync(client, "https://example.com/short-lived", alias: "short-lived", expiresAt: DateTimeOffset.UtcNow.AddSeconds(2));

        using var before = await GetAsync(client, "short-lived");
        await Task.Delay(TimeSpan.FromSeconds(3));
        using var after = await GetAsync(client, "short-lived");

        Assert.Equal(HttpStatusCode.Found, before.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, after.StatusCode);
        Assert.Equal(1, metrics.Total("shorty.cache.misses")); // the expiry was checked against the cached value, not by asking the database
    }

    [Fact]
    public async Task WhenRedisIsDown_RedirectsKeepWorkingFromPostgres_AndRecoverWhenItReturns()
    {
        await redis.FlushAsync();
        var connectionString = await postgres.CreateDatabaseAsync("outage");
        await using var creator = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
        using var creatorClient = creator.Client();
        var code = await CreateAsync(creatorClient, "https://example.com/survives");

        await redis.StopAsync();
        try
        {
            await using var instance = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
            using var client = instance.Client();

            // The first request also pays for noticing that Redis is gone; later ones must not wait for it at all.
            var firstTimer = Stopwatch.StartNew();
            using var first = await GetAsync(client, code);
            firstTimer.Stop();
            var laterTimer = Stopwatch.StartNew();
            for (var i = 0; i < 5; i++)
            {
                using var later = await GetAsync(client, code);
                Assert.Equal(HttpStatusCode.Found, later.StatusCode);
            }
            laterTimer.Stop();
            using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

            Assert.Equal(HttpStatusCode.Found, first.StatusCode);
            Assert.True(firstTimer.Elapsed < TimeSpan.FromSeconds(3), $"the first redirect took {firstTimer.Elapsed.TotalSeconds:0.0} s with Redis down");
            Assert.True(laterTimer.Elapsed < TimeSpan.FromSeconds(3), $"five redirects took {laterTimer.Elapsed.TotalSeconds:0.0} s with Redis down");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode); // readiness reports the outage
        }
        finally
        {
            await redis.StartAsync();
        }

        // After Redis returns, a new instance (so memory cannot help) reaches it again and caches in it.
        await using var recovered = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
        using var recoveredClient = recovered.Client();
        HttpStatusCode readiness = default;
        for (var attempt = 0; attempt < 30 && readiness != HttpStatusCode.OK; attempt++)
        {
            using var response = await recoveredClient.GetAsync(new Uri("/health/ready", UriKind.Relative));
            readiness = response.StatusCode;
            if (readiness != HttpStatusCode.OK) await Task.Delay(500);
        }
        Assert.Equal(HttpStatusCode.OK, readiness);
    }

    [Fact]
    public async Task ASlowRedis_DoesNotHoldRedirectsHostage()
    {
        await redis.FlushAsync();
        var connectionString = await postgres.CreateDatabaseAsync("slow");
        await using var creator = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
        using var creatorClient = creator.Client();
        var code = await CreateAsync(creatorClient, "https://example.com/slow-cache");

        await using var instance = ShortyInstance.Start(connectionString, redis: redis.ConnectionString);
        using var client = instance.Client();
        using var warmup = await GetAsync(client, "no-such-link"); // connects while Redis still answers

        await redis.PauseAsync(TimeSpan.FromSeconds(8)); // Redis stops answering: an overloaded server, not a dead one
        var timer = Stopwatch.StartNew();
        using var response = await GetAsync(client, code);
        timer.Stop();

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        // The command timeout is 250 ms. Waiting for the default five seconds, or for the pause to end, would fail this.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2.5), $"the redirect took {timer.Elapsed.TotalSeconds:0.0} s while Redis was unresponsive");
        await Task.Delay(TimeSpan.FromSeconds(8)); // let the pause end before the next test uses this Redis
    }

    [Fact]
    public async Task WithoutACache_EveryRedirectReadsPostgres()
    {
        await redis.FlushAsync();
        var connectionString = await postgres.CreateDatabaseAsync("nocache");
        using var metrics = new MetricsProbe();
        await using var instance = ShortyInstance.Start(connectionString, redis: redis.ConnectionString, settings: new Dictionary<string, string> { ["Cache:Mode"] = "None" });
        using var client = instance.Client();
        var code = await CreateAsync(client, "https://example.com/uncached");

        for (var i = 0; i < 10; i++)
        {
            using var response = await GetAsync(client, code);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        Assert.Equal(10, metrics.Total("shorty.cache.misses")); // the baseline the other modes are compared with
        Assert.Equal(0, await redis.KeyCountAsync());
    }

    [Fact]
    public async Task MemoryOnly_KeepsEachInstanceSeparate_AndNeverTouchesRedis()
    {
        await redis.FlushAsync();
        var connectionString = await postgres.CreateDatabaseAsync("memonly");
        using var metrics = new MetricsProbe();
        var settings = new Dictionary<string, string> { ["Cache:Mode"] = "MemoryOnly" };
        await using var first = ShortyInstance.Start(connectionString, redis: redis.ConnectionString, settings: settings);
        await using var second = ShortyInstance.Start(connectionString, redis: redis.ConnectionString, settings: settings);
        using var clientA = first.Client();
        using var clientB = second.Client();
        var code = await CreateAsync(clientA, "https://example.com/memory");

        (await GetAsync(clientA, code)).Dispose();
        (await GetAsync(clientA, code)).Dispose();
        (await GetAsync(clientB, code)).Dispose();
        (await GetAsync(clientB, code)).Dispose();

        Assert.Equal(2, metrics.Total("shorty.cache.misses")); // one read per instance: with Redis, the second instance would have found it there
        Assert.Equal(0, await redis.KeyCountAsync());
    }
}
