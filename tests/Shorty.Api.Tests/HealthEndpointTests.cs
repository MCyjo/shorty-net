using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Shorty.Api.Tests;

/// <summary>
/// Runs without Docker: the dependencies are deliberately unreachable, which is exactly the situation the probe
/// split is for. The liveness probe must not depend on them; the readiness probe must report them.
/// </summary>
public sealed class HealthEndpointTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b
            // WebApplicationFactory defaults to Development, which migrates the database at startup. These tests keep the
            // dependencies unreachable on purpose, so they run as in the cloud: Production, no migration.
            .UseEnvironment("Production")
            .UseSetting("OTEL_SDK_DISABLED", "true")
            .UseSetting("CodeGeneration:Seed", "1")
            // Nothing listens on port 1; short timeouts keep the test fast.
            .UseSetting("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1")
            .UseSetting("ConnectionStrings:Redis", "127.0.0.1:1,abortConnect=false,connectTimeout=500,syncTimeout=500"));

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Liveness_IsHealthy_EvenWhenEveryDependencyIsDown()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_IsUnhealthy_WhenPostgresAndRedisAreUnreachable()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
