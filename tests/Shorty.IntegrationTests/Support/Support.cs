using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using System.Net;
using System.Net.Sockets;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Shorty.IntegrationTests.Support;

/// <summary>One PostgreSQL container for the whole run; each test gets its own database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await command.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

/// <summary>
/// A Redis container on a fixed host port: the tests stop and start it to simulate an outage, and a restart with a random
/// port would hand the application an address that no longer exists.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly int _port = FreePort();
    private readonly RedisContainer _container;

    public RedisFixture() =>
        _container = new RedisBuilder("redis:7.2-alpine").WithPortBinding(_port, 6379).Build();

    public string ConnectionString => $"127.0.0.1:{_port},abortConnect=false,connectTimeout=1000";

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public Task StopAsync() => _container.StopAsync();

    public Task StartAsync() => _container.StartAsync();

    public Task FlushAsync() => _container.ExecAsync(["redis-cli", "FLUSHALL"]);

    /// <summary>Makes Redis accept connections but stop answering for a while, like a server that is overloaded.</summary>
    public Task PauseAsync(TimeSpan duration) =>
        _container.ExecAsync(["redis-cli", "CLIENT", "PAUSE", ((int)duration.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture), "ALL"]);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedisFixture>
{
    // One collection: the tests share process-wide metric counters, so they must not run in parallel.
    public const string Name = "postgres";
}

internal static class ShortyInstance
{
    /// <summary>One in-process copy of the application. Several of them against one database behave like scaled-out instances.</summary>
    public static WebApplicationFactory<Program> Start(string connectionString, int blockSize = 1000, string? redis = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("OTEL_SDK_DISABLED", "true")
            // Every instance has its own connection pool, so instances x pool size must stay under the server's max_connections
            // (100 here). Without a cap, three instances under load ask for up to 300 and the server answers 53300.
            .UseSetting("ConnectionStrings:Postgres", new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 20 }.ConnectionString)
            // Tests that do not care about the cache get an unreachable Redis: the redirect path must work without it anyway.
            .UseSetting("ConnectionStrings:Redis", redis ?? "127.0.0.1:1,abortConnect=false,connectTimeout=200")
            .UseSetting("CodeGeneration:Seed", "20260507")
            .UseSetting("Shorty:IdBlockSize", blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("Database:MigrateOnStartup", "true"));

    public static HttpClient Client(this WebApplicationFactory<Program> instance) =>
        instance.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}

/// <summary>Reads the application's own counters, so a test can assert on what the system reports about itself.</summary>
internal sealed class MetricsProbe : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _totals = [];
    private readonly Lock _gate = new();

    public MetricsProbe()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Shorty") listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (_gate) _totals[instrument.Name] = _totals.GetValueOrDefault(instrument.Name) + value;
        });
        _listener.Start();
    }

    public long Total(string name)
    {
        lock (_gate) return _totals.GetValueOrDefault(name);
    }

    public void Dispose() => _listener.Dispose();
}
