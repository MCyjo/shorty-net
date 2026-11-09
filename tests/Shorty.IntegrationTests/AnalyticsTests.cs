extern alias analytics;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shorty.Api.Analytics;
using Shorty.Api.Links;
using Shorty.Core.Analytics;
using Shorty.IntegrationTests.Support;
using AnalyticsExtensions = analytics::Shorty.Analytics.ClickAnalyticsExtensions;
using ClickStore = analytics::Shorty.Analytics.ClickStore;

namespace Shorty.IntegrationTests;

/// <summary>Clicks from the redirect, through Event Hubs (the emulator), to PostgreSQL, and what happens when a part of that is missing.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AnalyticsTests(PostgresFixture postgres, EventHubsFixture hubs)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static Dictionary<string, string> ApiSettings(string eventHubs, string hub, params (string Key, string Value)[] more)
    {
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:EventHubs"] = eventHubs,
            ["Analytics:EventHubName"] = hub,
        };
        foreach (var (key, value) in more) settings[key] = value;
        return settings;
    }

    private async Task<IHost> StartWorkerAsync(string databaseConnection, string hub)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = databaseConnection,
            ["ConnectionStrings:EventHubs"] = hubs.ConnectionString,
            ["ConnectionStrings:Checkpoints"] = hubs.CheckpointConnectionString,
            ["Analytics:EventHubName"] = hub,
            ["Analytics:CheckpointContainer"] = $"checkpoints-{hub}", // the saved positions belong to this test's hub, and survive a restart of the worker
            ["Analytics:FlushInterval"] = "00:00:00.200",
        };

        var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(values))
            .ConfigureServices((context, services) => AnalyticsExtensions.AddClickAnalytics(services, context.Configuration))
            .Build();
        await host.StartAsync();
        return host;
    }

    private static async Task<string> CreateLinkAsync(HttpClient client, string alias)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/links", UriKind.Relative), new { url = "https://example.com/target", alias });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateLinkResponse>())!.Code;
    }

    private static async Task ClickAsync(HttpClient client, string code, int times, string? referrer = null)
    {
        for (var i = 0; i < times; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/{code}", UriKind.Relative));
            if (referrer is not null) request.Headers.Add("Referer", referrer);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }
    }

    private static async Task<long> CountAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Task<long> StoredClicksAsync(string connectionString, string code) =>
        CountAsync(connectionString, "SELECT count(*) FROM click_events WHERE code = @code", ("code", code));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, MetricsProbe? metrics = null)
    {
        var timer = Stopwatch.StartNew();
        while (!await condition())
        {
            if (timer.Elapsed > Patience) Assert.Fail($"Timed out after {Patience.TotalSeconds:0} s waiting for: {what}" + (metrics is null ? "" : $" (recorded {metrics.Total("shorty.clicks.recorded")}, sent {metrics.Total("shorty.clicks.sent")}, send failed {metrics.Total("shorty.clicks.send_failed")}, stored {metrics.Total("shorty.clicks.stored")}, malformed {metrics.Total("shorty.clicks.malformed")})"));
            await Task.Delay(250);
        }
    }

    [Fact]
    public async Task EachRedirect_BecomesOneStoredClick_AndShowsUpInTheStats()
    {
        await hubs.EnsureStartedAsync();
        var hub = hubs.NextHub();
        var connectionString = await postgres.CreateDatabaseAsync("clicks");
        using var metrics = new MetricsProbe();
        await using var api = ShortyInstance.Start(connectionString, settings: ApiSettings(hubs.ConnectionString, hub));
        using var client = api.Client();
        using var worker = await StartWorkerAsync(connectionString, hub);
        var code = await CreateLinkAsync(client, "analytics-flow");

        await ClickAsync(client, code, 45, referrer: "https://news.example.org/some/post?id=7");
        await ClickAsync(client, code, 5);
        await WaitUntilAsync(async () => await StoredClicksAsync(connectionString, code) >= 50, "50 clicks stored", metrics);

        Assert.Equal(50, await StoredClicksAsync(connectionString, code));
        Assert.Equal(50, await CountAsync(connectionString, "SELECT count(DISTINCT event_id) FROM click_events"));
        Assert.Equal(45, await CountAsync(connectionString, "SELECT count(*) FROM click_events WHERE referrer_host = 'news.example.org'")); // the host, not the page
        Assert.Equal(5, await CountAsync(connectionString, "SELECT count(*) FROM click_events WHERE referrer_host IS NULL"));
        Assert.Equal(50, metrics.Total("shorty.clicks.sent"));
        Assert.Equal(50, metrics.Total("shorty.clicks.stored"));
        Assert.Equal(0, metrics.Total("shorty.clicks.dropped"));
        Assert.Equal(0, metrics.Total("shorty.clicks.duplicates"));

        var stats = (await client.GetFromJsonAsync<LinkStats>(new Uri($"/api/links/{code}/stats", UriKind.Relative), System.Text.Json.JsonSerializerOptions.Web))!;
        Assert.Equal(50, stats.TotalClicks);
        Assert.Equal(50, stats.Daily.Sum(d => d.Clicks));

        using var unknown = await client.GetAsync(new Uri("/api/links/no-such-link/stats", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ARestartedWorker_ResumesFromItsSavedPosition_InsteadOfReadingEverythingAgain()
    {
        await hubs.EnsureStartedAsync();
        var hub = hubs.NextHub();
        var connectionString = await postgres.CreateDatabaseAsync("resume");
        using var metrics = new MetricsProbe();
        await using var api = ShortyInstance.Start(connectionString, settings: ApiSettings(hubs.ConnectionString, hub));
        using var client = api.Client();
        var code = await CreateLinkAsync(client, "resume-me");

        using (var first = await StartWorkerAsync(connectionString, hub))
        {
            await ClickAsync(client, code, 30);
            await WaitUntilAsync(async () => await StoredClicksAsync(connectionString, code) >= 30, "30 clicks stored", metrics);
            await first.StopAsync();
        }

        await ClickAsync(client, code, 20); // clicked while no worker was running: they wait in the broker
        using var second = await StartWorkerAsync(connectionString, hub);
        await WaitUntilAsync(async () => await StoredClicksAsync(connectionString, code) >= 50, "all 50 clicks stored");
        await Task.Delay(TimeSpan.FromSeconds(2)); // time for anything wrongly re-read to arrive

        Assert.Equal(50, await StoredClicksAsync(connectionString, code));
        Assert.Equal(50, metrics.Total("shorty.clicks.stored"));
        Assert.Equal(0, metrics.Total("shorty.clicks.duplicates")); // had the first 30 been read again, they would be counted here
    }

    [Fact]
    public async Task AMalformedMessage_IsSkipped_AndDoesNotBlockTheClicksBehindIt()
    {
        await hubs.EnsureStartedAsync();
        var hub = hubs.NextHub();
        var connectionString = await postgres.CreateDatabaseAsync("poison");
        using var metrics = new MetricsProbe();
        await using var api = ShortyInstance.Start(connectionString, settings: ApiSettings(hubs.ConnectionString, hub));
        using var client = api.Client();
        var code = await CreateLinkAsync(client, "poison-pill");

        await using (var producer = new EventHubProducerClient(hubs.ConnectionString, hub))
        {
            using var batch = await producer.CreateBatchAsync();
            Assert.True(batch.TryAdd(new EventData(Encoding.UTF8.GetBytes("this is not a click"))));
            Assert.True(batch.TryAdd(new EventData(Encoding.UTF8.GetBytes("{\"eventId\":\"00000000-0000-0000-0000-000000000000\",\"code\":\"x\"}"))));
            await producer.SendAsync(batch);
        }
        await ClickAsync(client, code, 10);

        using var worker = await StartWorkerAsync(connectionString, hub);
        await WaitUntilAsync(async () => await StoredClicksAsync(connectionString, code) >= 10, "the 10 real clicks stored", metrics);

        Assert.Equal(10, await StoredClicksAsync(connectionString, code));
        Assert.Equal(2, metrics.Total("shorty.clicks.malformed"));
    }

    [Fact]
    public async Task TheStore_IgnoresARepeatedClick_AndKeepsClicksOutsideTheCreatedPartitions()
    {
        var connectionString = await postgres.CreateDatabaseAsync("store");
        await using var api = ShortyInstance.Start(connectionString); // creates the schema
        using var client = api.Client();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new ClickStore(dataSource);

        var now = DateTimeOffset.UtcNow;
        var click = new ClickEvent(Guid.NewGuid(), "abc1234", now, null);
        var ancient = new ClickEvent(Guid.NewGuid(), "abc1234", now.AddYears(-3), null); // no partition for this month exists

        // Two workers starting together create the same partitions without failing. Last month, this one and two ahead: four.
        await Task.WhenAll(
            store.EnsurePartitionsAsync(now, 2, CancellationToken.None),
            store.EnsurePartitionsAsync(now, 2, CancellationToken.None));
        await store.EnsurePartitionsAsync(now, 2, CancellationToken.None); // and running it again changes nothing
        Assert.Equal(4, await CountAsync(connectionString,
            "SELECT count(*) FROM pg_inherits WHERE inhparent = 'click_events'::regclass AND inhrelid <> 'click_events_default'::regclass"));

        var first = await store.InsertAsync([click, ancient], CancellationToken.None);
        var again = await store.InsertAsync([click, ancient], CancellationToken.None); // the broker delivers at least once

        Assert.Equal(2, first);
        Assert.Equal(0, again);
        Assert.Equal(2, await CountAsync(connectionString, "SELECT count(*) FROM click_events"));
        Assert.Equal(1, await CountAsync(connectionString, "SELECT count(*) FROM click_events_default")); // the three-year-old click is kept, not refused
    }

    [Fact]
    public async Task WhenTheBrokerIsDown_RedirectsStayFast_AndLostClicksAreCountedNotHidden()
    {
        var connectionString = await postgres.CreateDatabaseAsync("nobroker");
        using var metrics = new MetricsProbe();
        await using var api = ShortyInstance.Start(connectionString, settings: ApiSettings(
            "Endpoint=sb://127.0.0.1:1;SharedAccessKeyName=k;SharedAccessKey=v;UseDevelopmentEmulator=true;", "clicks-0",
            ("Analytics:QueueCapacity", "20"), ("Analytics:MaxBatchSize", "5"), ("Analytics:SendTimeout", "00:00:01")));
        using var client = api.Client();
        var code = await CreateLinkAsync(client, "no-broker");

        var timer = Stopwatch.StartNew();
        await ClickAsync(client, code, 200);
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"200 redirects took {timer.Elapsed.TotalSeconds:0.0} s with the broker down");
        Assert.True(metrics.Total("shorty.clicks.dropped") > 0, $"a full queue must be visible as dropped clicks (recorded {metrics.Total("shorty.clicks.recorded")}, dropped {metrics.Total("shorty.clicks.dropped")}, sent {metrics.Total("shorty.clicks.sent")}, failed {metrics.Total("shorty.clicks.send_failed")}, took {timer.Elapsed.TotalSeconds:0.0} s)");
        Assert.Equal(200, metrics.Total("shorty.clicks.recorded") + metrics.Total("shorty.clicks.dropped")); // every click is either queued or counted as dropped
        await WaitUntilAsync(() => Task.FromResult(metrics.Total("shorty.clicks.send_failed") > 0), "a failed send to be counted");
    }
}
