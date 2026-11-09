using Npgsql;
using Shorty.Analytics;
using Shorty.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddShortyServiceDefaults("shorty-analytics");

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Postgres.");

builder.Services.AddClickAnalytics(builder.Configuration);

// Probes only: this process serves no traffic, but the platform needs to know it is alive and can reach PostgreSQL.
builder.Services.AddHealthChecks()
    .AddCheck("postgres", () =>
    {
        using var connection = new NpgsqlConnection(postgres);
        connection.Open();
        return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
    }, tags: [ServiceDefaultsExtensions.ReadyTag], timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();
app.MapShortyHealthChecks();
app.Run();
