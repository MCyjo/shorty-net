using System.Text.Json;
using System.Threading.Channels;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Shorty.Core;
using Shorty.Core.Analytics;

namespace Shorty.Api.Analytics;

public sealed class AnalyticsSettings
{
    public const string Section = "Analytics";

    /// <summary>The event hub clicks are published to.</summary>
    public string EventHubName { get; set; } = "clicks";

    /// <summary>How many clicks may wait in memory. When it is full, new clicks are dropped (and counted), never waited for.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>The most clicks sent to the broker in one batch.</summary>
    public int MaxBatchSize { get; set; } = 200;

    /// <summary>How long one attempt to reach the broker may take. With one retry, a broker that is down costs about twice this per batch.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a partly filled batch waits for more clicks before it is sent anyway.</summary>
    public TimeSpan MaxBatchDelay { get; set; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>Where the redirect path reports a click. It must never block and never throw.</summary>
internal interface IClickSink
{
    void Record(string code, string? referrerHost);
}

/// <summary>Used when no broker is configured: clicks are simply not recorded.</summary>
internal sealed class NullClickSink : IClickSink
{
    public void Record(string code, string? referrerHost)
    {
    }
}

/// <summary>
/// Takes clicks from the redirect path into a bounded in-memory queue. Writing to it is a few instructions and never waits,
/// so analytics cannot slow a redirect down; the price is that clicks are lost if the process dies with a full queue, or
/// when the queue overflows. Both are counted.
/// </summary>
internal sealed class ChannelClickSink : IClickSink
{
    private readonly Channel<ClickEvent> _queue;
    private readonly TimeProvider _clock;

    public ChannelClickSink(Microsoft.Extensions.Options.IOptions<AnalyticsSettings> settings, TimeProvider clock)
    {
        _clock = clock;
        _queue = Channel.CreateBounded<ClickEvent>(new BoundedChannelOptions(settings.Value.QueueCapacity)
        {
            // Wait, not DropWrite: with the Drop modes TryWrite reports success even when it throws the item away, so a full queue
            // would lose clicks without a trace. With Wait, TryWrite returns false when full (it never actually waits).
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public ChannelReader<ClickEvent> Reader => _queue.Reader;

    public void Record(string code, string? referrerHost)
    {
        var click = new ClickEvent(Guid.CreateVersion7(), code, _clock.GetUtcNow(), referrerHost);
        if (_queue.Writer.TryWrite(click)) ShortyMetrics.ClicksRecorded.Add(1);
        else ShortyMetrics.ClicksDropped.Add(1);
    }
}

/// <summary>Sends queued clicks to Event Hubs in batches. A broker that is down costs clicks, never redirects.</summary>
internal sealed partial class ClickPublisher(
    ChannelClickSink sink, EventHubProducerClient producer, Microsoft.Extensions.Options.IOptions<AnalyticsSettings> settings, ILogger<ClickPublisher> logger)
    : BackgroundService
{
    private long _lastWarning = long.MinValue / 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = settings.Value;
        var reader = sink.Reader;
        var pending = new List<ClickEvent>(options.MaxBatchSize);

        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                pending.Clear();
                using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                window.CancelAfter(options.MaxBatchDelay);
                try
                {
                    // Take what is there; if the batch is not full, wait a moment for more rather than sending one click at a time.
                    while (pending.Count < options.MaxBatchSize && await reader.WaitToReadAsync(window.Token))
                        while (pending.Count < options.MaxBatchSize && reader.TryRead(out var click)) pending.Add(click);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // the batch window ended: send what we have
                }

                await SendAsync(pending, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down: drain what is already queued, once, below
        }

        pending.Clear();
        while (pending.Count < options.MaxBatchSize && reader.TryRead(out var click)) pending.Add(click);
        if (pending.Count > 0) await SendAsync(pending, CancellationToken.None);
    }

    private async Task SendAsync(List<ClickEvent> clicks, CancellationToken ct)
    {
        if (clicks.Count == 0) return;
        try
        {
            var remaining = new Queue<ClickEvent>(clicks);
            while (remaining.Count > 0)
            {
                using var batch = await producer.CreateBatchAsync(ct); // no partition key: clicks are summed, so spreading them evenly is fine
                var inBatch = 0;
                while (remaining.TryPeek(out var click))
                {
                    var data = new EventData(JsonSerializer.SerializeToUtf8Bytes(click, JsonSerializerOptions.Web));
                    if (!batch.TryAdd(data)) break;
                    remaining.Dequeue();
                    inBatch++;
                }
                if (inBatch == 0) throw new InvalidOperationException("A click is larger than an empty batch.");
                await producer.SendAsync(batch, ct);
                ShortyMetrics.ClicksSent.Add(inBatch);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ShortyMetrics.ClicksSendFailed.Add(clicks.Count);
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastWarning);
            if (now - last >= 30_000 && Interlocked.CompareExchange(ref _lastWarning, now, last) == last)
                LogSendFailed(ex.GetType().Name, ex.Message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await producer.DisposeAsync();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send clicks to the broker ({Error}: {Message}). They are lost; counted in shorty.clicks.send_failed. Logged at most every 30 seconds.")]
    private partial void LogSendFailed(string error, string message);
}

internal static class ClickPublishingExtensions
{
    public static void AddShortyClickPublishing(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<AnalyticsSettings>(builder.Configuration.GetSection(AnalyticsSettings.Section));

        var connection = builder.Configuration.GetConnectionString("EventHubs");
        if (string.IsNullOrWhiteSpace(connection))
        {
            // Analytics is optional: without a broker the API runs as before and records nothing.
            builder.Services.AddSingleton<IClickSink, NullClickSink>();
            return;
        }

        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnalyticsSettings>>().Value;
            var options = new EventHubProducerClientOptions
            {
                // Bounded retries: the producer must give up, so the queue behind it drains or drops instead of waiting for minutes.
                RetryOptions = new EventHubsRetryOptions { MaximumRetries = 1, TryTimeout = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnalyticsSettings>>().Value.SendTimeout, Delay = TimeSpan.FromMilliseconds(200) },
            };
            // A connection string (local, emulator) or just the namespace address, in which case the managed identity signs in.
            return BrokerConnection.IsEventHubsConnectionString(connection)
                ? new EventHubProducerClient(connection, settings.EventHubName, options)
                : new EventHubProducerClient(connection, settings.EventHubName, new DefaultAzureCredential(), options);
        });
        builder.Services.AddSingleton<ChannelClickSink>();
        builder.Services.AddSingleton<IClickSink>(sp => sp.GetRequiredService<ChannelClickSink>());
        builder.Services.AddHostedService<ClickPublisher>();
    }
}
