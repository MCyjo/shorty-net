using Npgsql;
using Shorty.Api;
using Shorty.Api.Health;
using Shorty.Api.Links;
using Shorty.Api.Persistence;
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

// abortConnect=false: the multiplexer keeps retrying in the background, so a Redis that is down at startup (or later)
// makes /health/ready fail instead of preventing the process from starting.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));

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

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    // Convenient for a demo, and safe with several instances starting together (an advisory lock serialises them).
    // In production run migrations as a separate deployment step instead and leave the flag off.
    foreach (var version in await MigrationRunner.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Lifetime.ApplicationStopping))
        app.Logger.MigrationApplied(version);
}

app.MapShortyHealthChecks();
app.MapLinkEndpoints();

app.Run();

public partial class Program;
