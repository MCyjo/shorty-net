using Microsoft.Extensions.Caching.StackExchangeRedis;
using Npgsql;
using Shorty.Api;
using Shorty.Api.Caching;
using Shorty.Api.Health;
using Shorty.Api.Links;
using Shorty.Api.Persistence;
using Shorty.Api.RateLimiting;
using Shorty.Core;
using Shorty.ServiceDefaults;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddShortyServiceDefaults("shorty-api");

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Postgres.");
var redis = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Redis.");
var seed = builder.Configuration.GetValue<ulong?>("CodeGeneration:Seed")
    ?? throw new InvalidOperationException("Configure CodeGeneration:Seed (a number that stays the same for the life of the data).");
var blockSize = builder.Configuration.GetValue("Shorty:IdBlockSize", 1000);

builder.Services.AddNpgsqlDataSource(postgres);

// Redis is a cache, so it must never make a request slow or fail:
//  - abortConnect=false: a Redis that is down at startup (or later) fails /health/ready instead of stopping the process.
//  - FailFast: while disconnected, commands fail at once. The default queues them for the whole timeout, which would hold
//    every redirect for seconds while the cache is down.
//  - a short command timeout: a slow Redis is treated like an absent one, and the request goes to PostgreSQL.
var redisOptions = ConfigurationOptions.Parse(redis);
redisOptions.AbortOnConnectFail = false;
redisOptions.BacklogPolicy = BacklogPolicy.FailFast;
redisOptions.AsyncTimeout = builder.Configuration.GetValue("Cache:RedisTimeoutMs", 250);
//  - one connection attempt: the default is three, each as long as connectTimeout, which held the first request for seconds.
redisOptions.ConnectRetry = 1;
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));

// Two layers: each instance's memory (L1) and Redis shared by all instances (L2). HybridCache also makes concurrent requests
// for the same missing key share one database read. L2 reuses the multiplexer above instead of opening a second connection.
builder.Services.Configure<CacheSettings>(builder.Configuration.GetSection(CacheSettings.Section));
builder.Services.AddOptions<RedisCacheOptions>()
    .Configure<IConnectionMultiplexer>((options, multiplexer) => options.ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer));
builder.Services.AddStackExchangeRedisCache(_ => { });
builder.Services.AddHybridCache();
builder.Services.AddSingleton<LinkCache>();

builder.AddShortyRateLimiting();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IIdBlockStore, PostgresIdBlockStore>();
builder.Services.AddSingleton(sp => new BlockIdSource(sp.GetRequiredService<IIdBlockStore>(), blockSize));
builder.Services.AddSingleton(new ShortCodeGenerator(new CodeScrambler(seed)));
builder.Services.AddSingleton<ILinkStore, PostgresLinkStore>();
builder.Services.AddSingleton<LinkService>();

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: [ServiceDefaultsExtensions.ReadyTag], timeout: TimeSpan.FromSeconds(3))
    .AddCheck<RedisHealthCheck>("redis", tags: [ServiceDefaultsExtensions.ReadyTag], timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();

// Connect to Redis now, at startup, rather than inside the first user request. If Redis is down this costs one connect timeout
// here (the multiplexer keeps retrying in the background); it never costs a visitor.
_ = app.Services.GetRequiredService<IConnectionMultiplexer>();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    // Convenient for a demo, and safe with several instances starting together (an advisory lock serialises them).
    // In production run migrations as a separate deployment step instead and leave the flag off.
    foreach (var version in await MigrationRunner.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Lifetime.ApplicationStopping))
        app.Logger.MigrationApplied(version);
}

app.UseShortyRateLimiting();
app.MapShortyHealthChecks();
app.MapLinkEndpoints();

app.Run();

public partial class Program;
