using Testcontainers.Azurite;
using Testcontainers.EventHubs;

namespace Shorty.IntegrationTests.Support;

/// <summary>
/// The Azure Event Hubs emulator, started the first time a test needs it (most tests never do). Every test gets its own event
/// hub, so clicks published by one test are never read by another. Starting it means accepting Microsoft's emulator licence
/// terms: https://github.com/Azure/azure-event-hubs-emulator-installer/blob/main/EMULATOR_EULA.md
/// </summary>
public sealed class EventHubsFixture : IAsyncLifetime
{
    public const int Hubs = 8;

    private readonly Lazy<Task> _start;
    private EventHubsContainer? _hubs;
    private AzuriteContainer? _blobs;
    private int _next = -1;

    public EventHubsFixture() => _start = new Lazy<Task>(StartAsync);

    public string ConnectionString => _hubs?.GetConnectionString() ?? throw new InvalidOperationException("Call EnsureStartedAsync first.");

    /// <summary>A blob store for the consumer's saved positions (a second Azurite: the emulator's own is not reachable from the tests).</summary>
    public string CheckpointConnectionString => _blobs?.GetConnectionString() ?? throw new InvalidOperationException("Call EnsureStartedAsync first.");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_hubs is not null) await _hubs.DisposeAsync();
        if (_blobs is not null) await _blobs.DisposeAsync();
    }

    public Task EnsureStartedAsync() => _start.Value;

    private async Task StartAsync()
    {
        var configuration = EventHubsServiceConfiguration.Create();
        for (var i = 0; i < Hubs; i++) configuration = configuration.WithEntity($"clicks-{i}", 2, "analytics");

        // The SDK is newer than the Azurite image: without this Azurite refuses its API version.
        var blobs = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").WithCommand("--skipApiVersionCheck").Build();
        var hubs = new EventHubsBuilder("mcr.microsoft.com/azure-messaging/eventhubs-emulator:latest")
            .WithAcceptLicenseAgreement(true)
            .WithConfigurationBuilder(configuration)
            .Build();
        await Task.WhenAll(blobs.StartAsync(), hubs.StartAsync());
        (_blobs, _hubs) = (blobs, hubs);
    }

    /// <summary>The next unused event hub name.</summary>
    public string NextHub() => $"clicks-{Interlocked.Increment(ref _next)}";
}
