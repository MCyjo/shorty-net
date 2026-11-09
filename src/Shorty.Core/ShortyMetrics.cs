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

    public static readonly Counter<long> BlocksAllocated =
        Meter.CreateCounter<long>("shorty.id.blocks_allocated", description: "Id blocks reserved from the shared store.");
}
