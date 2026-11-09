using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Processor;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using Shorty.Core;
using Shorty.Core.Analytics;

namespace Shorty.Analytics;

public sealed class WorkerSettings
{
    public const string Section = "Analytics";

    public string EventHubName { get; set; } = "clicks";

    public string ConsumerGroup { get; set; } = "analytics";

    /// <summary>The blob container holding each partition's position, so a restarted worker carries on where it stopped.</summary>
    public string CheckpointContainer { get; set; } = "shorty-checkpoints";

    /// <summary>Clicks are written in groups of up to this many.</summary>
    public int MaxBatchSize { get; set; } = 200;

    /// <summary>A partly filled group is written after this long, so a quiet period does not hold clicks back.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int PartitionsAheadMonths { get; set; } = 2;
}

/// <summary>
/// Reads clicks from Event Hubs and stores them in PostgreSQL. A partition's position is saved only after its clicks are
/// committed, so a crash re-delivers clicks (the store ignores the repeats) and never loses them.
/// </summary>
internal sealed partial class ClickConsumer(
    ClickStore store, IOptions<WorkerSettings> settings, IConfiguration configuration, TimeProvider clock, ILogger<ClickConsumer> logger)
    : BackgroundService
{
    private sealed class PartitionBuffer
    {
        public List<ClickEvent> Clicks { get; } = [];
        public ProcessEventArgs? Last { get; set; }
        public long FirstAddedAt { get; set; }
    }

    private readonly Dictionary<string, PartitionBuffer> _buffers = [];
    private readonly Lock _gate = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = settings.Value;
        var eventHubs = configuration.GetConnectionString("EventHubs")
            ?? throw new InvalidOperationException("Configure ConnectionStrings:EventHubs.");
        var checkpoints = configuration.GetConnectionString("Checkpoints")
            ?? throw new InvalidOperationException("Configure ConnectionStrings:Checkpoints (a blob storage connection string).");

        // The partition for this month must exist before the first click is written: PostgreSQL refuses to create it later
        // if the default partition already holds clicks that belong to it.
        await store.EnsurePartitionsAsync(clock.GetUtcNow(), options.PartitionsAheadMonths, stoppingToken);

        var container = new BlobContainerClient(checkpoints, options.CheckpointContainer);
        await container.CreateIfNotExistsAsync(cancellationToken: stoppingToken);

        var processor = new EventProcessorClient(container, options.ConsumerGroup, eventHubs, options.EventHubName,
            new EventProcessorClientOptions
            {
                // Without this the handler is only called when an event arrives, and a quiet partition would never flush.
                MaximumWaitTime = options.FlushInterval,
                LoadBalancingStrategy = LoadBalancingStrategy.Greedy,
            });
        // With no saved position (the first start ever, or a new consumer group) read from the oldest event the broker still has.
        // The default is "only new events", which would silently skip every click published before the worker first ran.
        processor.PartitionInitializingAsync += args =>
        {
            args.DefaultStartingPosition = EventPosition.Earliest;
            return Task.CompletedTask;
        };
        processor.ProcessEventAsync += HandleAsync;
        processor.ProcessErrorAsync += args =>
        {
            LogProcessorError(args.Operation, args.PartitionId, args.Exception.GetType().Name, args.Exception.Message);
            return Task.CompletedTask;
        };
        processor.PartitionClosingAsync += args =>
        {
            // Unsaved clicks are not lost: the next owner of the partition starts from the last saved position.
            lock (_gate) _ = _buffers.Remove(args.PartitionId);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromHours(6), clock, stoppingToken);
                await store.EnsurePartitionsAsync(clock.GetUtcNow(), options.PartitionsAheadMonths, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // stopping
        }
        finally
        {
            await processor.StopProcessingAsync(CancellationToken.None);
        }
    }

    private async Task HandleAsync(ProcessEventArgs args)
    {
        var options = settings.Value;
        PartitionBuffer buffer;
        lock (_gate)
        {
            if (!_buffers.TryGetValue(args.Partition.PartitionId, out buffer!))
                _buffers[args.Partition.PartitionId] = buffer = new PartitionBuffer();
        }

        if (args.HasEvent)
        {
            if (TryParse(args.Data, out var click))
            {
                if (buffer.Clicks.Count == 0) buffer.FirstAddedAt = Environment.TickCount64;
                buffer.Clicks.Add(click);
            }
            else
            {
                ShortyMetrics.ClicksMalformed.Add(1);
            }
            buffer.Last = args; // the position to save once the clicks so far are committed
        }

        var waitedLongEnough = buffer.Clicks.Count > 0 && Environment.TickCount64 - buffer.FirstAddedAt >= options.FlushInterval.TotalMilliseconds;
        var full = buffer.Clicks.Count >= options.MaxBatchSize;
        // No event means the wait time passed with nothing to read: a good moment to write what has collected.
        var idle = !args.HasEvent && buffer.Clicks.Count > 0;
        var skippedOnly = buffer.Clicks.Count == 0 && buffer.Last is not null;
        if (!(full || waitedLongEnough || idle || skippedOnly)) return;

        await FlushAsync(args.Partition.PartitionId, buffer, args.CancellationToken);
    }

    private async Task FlushAsync(string partitionId, PartitionBuffer buffer, CancellationToken ct)
    {
        // Retry until the database accepts the clicks. Giving up here would lose them (the processor moves on after a
        // handler fails); waiting holds this partition back, and the clicks wait safely in the broker meanwhile.
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                if (buffer.Clicks.Count > 0)
                {
                    var inserted = await store.InsertAsync(buffer.Clicks, ct);
                    ShortyMetrics.ClicksStored.Add(inserted);
                    ShortyMetrics.ClicksDuplicate.Add(buffer.Clicks.Count - inserted);
                }
                if (buffer.Last is not null)
                    await buffer.Last.Value.UpdateCheckpointAsync(ct);
                buffer.Clicks.Clear();
                buffer.Last = null;
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogStoreFailed(partitionId, ex.GetType().Name, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
            }
        }
    }

    private static bool TryParse(EventData data, out ClickEvent click)
    {
        try
        {
            click = JsonSerializer.Deserialize<ClickEvent>(data.EventBody.ToMemory().Span, JsonSerializerOptions.Web)!;
            return click is { Code.Length: > 0 } && click.EventId != Guid.Empty;
        }
        catch (JsonException)
        {
            click = null!;
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event Hubs processor error during {Operation} (partition {PartitionId}): {Error}: {Message}")]
    private partial void LogProcessorError(string operation, string partitionId, string error, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not store clicks for partition {PartitionId} ({Error}: {Message}); retrying in {Seconds} s. The clicks stay in the broker.")]
    private partial void LogStoreFailed(string partitionId, string error, string message, double seconds);
}
