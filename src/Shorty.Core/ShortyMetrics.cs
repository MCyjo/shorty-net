using System.Diagnostics.Metrics;

namespace Shorty.Core;

public static class ShortyMetrics
{
    public const string MeterName = "Shorty";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> LinksCreated =
        Meter.CreateCounter<long>("shorty.links.created", description: "Short links created.");

    /// <summary>
    /// A generated code that already existed. With ids that are unique by construction this should stay at zero; a value
    /// above zero means two instances were given overlapping ids (or a custom alias took a code first).
    /// </summary>
    public static readonly Counter<long> CodeCollisions =
        Meter.CreateCounter<long>("shorty.code.collisions", description: "Generated codes that were already taken.");

    /// <summary>Redirect lookups that were not served from memory or Redis and had to read PostgreSQL.</summary>
    public static readonly Counter<long> CacheMisses =
        Meter.CreateCounter<long>("shorty.cache.misses", description: "Lookups that had to read PostgreSQL.");

    /// <summary>Every redirect request, tagged with its outcome: redirect, not_found or expired. Hits = redirects - cache misses.</summary>
    public static readonly Counter<long> Redirects =
        Meter.CreateCounter<long>("shorty.redirects", description: "Redirect requests, by outcome.");

    /// <summary>Requests refused with 429, tagged with the policy that refused them.</summary>
    public static readonly Counter<long> RateLimited =
        Meter.CreateCounter<long>("shorty.ratelimit.rejected", description: "Requests refused by a rate limit, by policy.");

    /// <summary>Clicks accepted into the in-memory queue, waiting to be sent to the broker.</summary>
    public static readonly Counter<long> ClicksRecorded =
        Meter.CreateCounter<long>("shorty.clicks.recorded", description: "Clicks queued for the broker.");

    /// <summary>Clicks thrown away because the queue was full (the broker is slower than the traffic, or down). Counted, never silent.</summary>
    public static readonly Counter<long> ClicksDropped =
        Meter.CreateCounter<long>("shorty.clicks.dropped", description: "Clicks dropped because the queue was full.");

    public static readonly Counter<long> ClicksSent =
        Meter.CreateCounter<long>("shorty.clicks.sent", description: "Clicks delivered to the broker.");

    /// <summary>Clicks that could not be delivered to the broker after its retries, and were discarded.</summary>
    public static readonly Counter<long> ClicksSendFailed =
        Meter.CreateCounter<long>("shorty.clicks.send_failed", description: "Clicks lost because the broker did not accept them.");

    /// <summary>Clicks the worker wrote to PostgreSQL.</summary>
    public static readonly Counter<long> ClicksStored =
        Meter.CreateCounter<long>("shorty.clicks.stored", description: "Clicks written by the analytics worker.");

    /// <summary>Events the worker could not read as a click. They are skipped (and counted) so one bad message cannot block a partition.</summary>
    public static readonly Counter<long> ClicksMalformed =
        Meter.CreateCounter<long>("shorty.clicks.malformed", description: "Broker messages that were not a valid click.");

    /// <summary>Clicks the worker received again after it had stored them: delivery is at least once, so this is expected, and harmless.</summary>
    public static readonly Counter<long> ClicksDuplicate =
        Meter.CreateCounter<long>("shorty.clicks.duplicates", description: "Redelivered clicks that were already stored.");

    public static readonly Counter<long> BlocksAllocated =
        Meter.CreateCounter<long>("shorty.id.blocks_allocated", description: "Id blocks reserved from the shared store.");
}
