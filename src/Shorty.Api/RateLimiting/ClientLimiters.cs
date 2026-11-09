using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace Shorty.Api.RateLimiting;

public static class ClientLimiters
{
    /// <summary>Builds the limiter for one client under the given settings.</summary>
    public static RateLimitPartition<string> For(string clientKey, LimiterSettings settings) => settings.Algorithm switch
    {
        LimitAlgorithm.FixedWindow => RateLimitPartition.GetFixedWindowLimiter(clientKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = settings.Limit,
            Window = settings.Window,
            QueueLimit = 0, // reject at once: a queued redirect is a slow redirect
            AutoReplenishment = true,
        }),
        LimitAlgorithm.SlidingWindow => RateLimitPartition.GetSlidingWindowLimiter(clientKey, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = settings.Limit,
            Window = settings.Window,
            SegmentsPerWindow = settings.Segments,
            QueueLimit = 0,
            AutoReplenishment = true,
        }),
        // Limit tokens per Window, handed out one at a time: the steady rate is Limit / Window, the largest burst is Limit.
        LimitAlgorithm.TokenBucket => RateLimitPartition.GetTokenBucketLimiter(clientKey, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = settings.Limit,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = settings.Window / settings.Limit,
            QueueLimit = 0,
            AutoReplenishment = true,
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Algorithm, "Unknown algorithm."),
    };

    /// <summary>
    /// Who counts as one client. An IPv6 user normally owns a whole /64, so limiting single addresses would let them
    /// rotate through billions of them; those are grouped by their /64 instead.
    /// </summary>
    public static string ClientKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return $"{new IPAddress(bytes)}/64";
    }
}
