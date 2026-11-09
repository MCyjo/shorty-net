using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

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

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    // One collection: the tests share process-wide metric counters, so they must not run in parallel.
    public const string Name = "postgres";
}

internal static class ShortyInstance
{
    /// <summary>One in-process copy of the application. Several of them against one database behave like scaled-out instances.</summary>
    public static WebApplicationFactory<Program> Start(string connectionString, int blockSize = 1000) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("OTEL_SDK_DISABLED", "true")
            // Every instance has its own connection pool, so instances x pool size must stay under the server's max_connections
            // (100 here). Without a cap, three instances under load ask for up to 300 and the server answers 53300.
            .UseSetting("ConnectionStrings:Postgres", new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 20 }.ConnectionString)
            // Never contacted by these tests: the cache is not used yet and readiness is not probed.
            .UseSetting("ConnectionStrings:Redis", "127.0.0.1:1,abortConnect=false,connectTimeout=200")
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
