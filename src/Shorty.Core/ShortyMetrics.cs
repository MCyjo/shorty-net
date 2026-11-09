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

    public static readonly Counter<long> BlocksAllocated =
        Meter.CreateCounter<long>("shorty.id.blocks_allocated", description: "Id blocks reserved from the shared store.");
}
