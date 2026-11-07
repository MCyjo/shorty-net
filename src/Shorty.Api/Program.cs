using Shorty.Api.Health;
using Shorty.ServiceDefaults;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddShortyServiceDefaults("shorty-api");

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Postgres.");
var redis = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Redis.");

builder.Services.AddNpgsqlDataSource(postgres);

// abortConnect=false: the multiplexer keeps retrying in the background, so a Redis that is down at startup (or later)
// makes /health/ready fail instead of preventing the process from starting.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: [ServiceDefaultsExtensions.ReadyTag], timeout: TimeSpan.FromSeconds(3))
    .AddCheck<RedisHealthCheck>("redis", tags: [ServiceDefaultsExtensions.ReadyTag], timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();

app.MapShortyHealthChecks();

app.Run();

public partial class Program;
