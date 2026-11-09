using System.Net;
using System.Threading.RateLimiting;
using Shorty.Api.RateLimiting;

namespace Shorty.Api.Tests;

/// <summary>
/// How the three algorithms actually behave, measured with the real limiters and real time (no Docker needed). The
/// windows are a couple of seconds, so each test takes a few seconds; the margins are wide enough not to depend on timer precision.
/// </summary>
public sealed class RateLimitAlgorithmTests
{
    private static PartitionedRateLimiter<string> Limiter(LimiterSettings settings) =>
        PartitionedRateLimiter.Create<string, string>(key => ClientLimiters.For(key, settings));

    private static int Burst(PartitionedRateLimiter<string> limiter, string client, int attempts)
    {
        var accepted = 0;
        for (var i = 0; i < attempts; i++)
        {
            using var lease = limiter.AttemptAcquire(client);
            if (lease.IsAcquired) accepted++;
        }
        return accepted;
    }

    /// <summary>One request to start the window, a burst at its very end, and another burst just after it ends.</summary>
    private static async Task<(int EndOfWindow, int StartOfNext)> BurstsAcrossABoundaryAsync(
        PartitionedRateLimiter<string> limiter, int limit, TimeSpan window)
    {
        Burst(limiter, "client", 1);                                        // the limiter, and so the first window, starts now
        await Task.Delay(window - TimeSpan.FromMilliseconds(250));
        var endOfWindow = Burst(limiter, "client", limit - 1);              // the rest of the allowance, just before the boundary
        await Task.Delay(TimeSpan.FromMilliseconds(500));                   // now 250 ms into the next window
        var startOfNext = Burst(limiter, "client", limit);
        return (endOfWindow, startOfNext);
    }

    [Fact]
    public async Task FixedWindow_AllowsTwiceTheLimit_WithinHalfASecondAroundABoundary()
    {
        using var limiter = Limiter(new LimiterSettings { Algorithm = LimitAlgorithm.FixedWindow, Limit = 10, Window = TimeSpan.FromSeconds(2) });

        var (endOfWindow, startOfNext) = await BurstsAcrossABoundaryAsync(limiter, 10, TimeSpan.FromSeconds(2));

        Assert.Equal(9, endOfWindow);
        Assert.Equal(10, startOfNext); // 19 more requests in half a second, plus the first: nearly double the limit of 10 per 2 s
    }

    [Fact]
    public async Task SlidingWindow_DoesNotAllowTheBoundaryBurst()
    {
        using var limiter = Limiter(new LimiterSettings { Algorithm = LimitAlgorithm.SlidingWindow, Limit = 10, Window = TimeSpan.FromSeconds(2), Segments = 4 });

        var (endOfWindow, startOfNext) = await BurstsAcrossABoundaryAsync(limiter, 10, TimeSpan.FromSeconds(2));

        Assert.Equal(9, endOfWindow);
        // Only the allowance of the first request (one segment) came back; the burst at the end of the window still counts.
        Assert.InRange(startOfNext, 0, 2);
    }

    [Fact]
    public async Task TokenBucket_AllowsABurstOfTheBucketSize_ThenTheSteadyRate()
    {
        // 5 tokens per 2.5 s: a bucket of 5, one token back every 500 ms.
        using var limiter = Limiter(new LimiterSettings { Algorithm = LimitAlgorithm.TokenBucket, Limit = 5, Window = TimeSpan.FromSeconds(2.5) });

        var burst = Burst(limiter, "client", 20);
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var afterWaiting = Burst(limiter, "client", 20);

        Assert.Equal(5, burst);
        Assert.InRange(afterWaiting, 1, 3); // about two tokens came back in 1.2 s, not five
    }

    [Theory]
    [InlineData(LimitAlgorithm.FixedWindow)]
    [InlineData(LimitAlgorithm.SlidingWindow)]
    [InlineData(LimitAlgorithm.TokenBucket)]
    public void EachClientHasItsOwnAllowance(LimitAlgorithm algorithm)
    {
        using var limiter = Limiter(new LimiterSettings { Algorithm = algorithm, Limit = 3, Window = TimeSpan.FromMinutes(30) });

        var first = Burst(limiter, "203.0.113.1", 10);
        var second = Burst(limiter, "203.0.113.2", 10);

        Assert.Equal(3, first);
        Assert.Equal(3, second); // the first client using up its allowance took nothing from the second
    }

    [Fact]
    public void ClientKey_UsesTheAddressForIPv4_AndTheSlash64ForIPv6()
    {
        Assert.Equal("203.0.113.7", ClientLimiters.ClientKey(IPAddress.Parse("203.0.113.7")));
        Assert.Equal("203.0.113.7", ClientLimiters.ClientKey(IPAddress.Parse("::ffff:203.0.113.7")));
        Assert.Equal("unknown", ClientLimiters.ClientKey(null));

        var a = ClientLimiters.ClientKey(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        var sameNetwork = ClientLimiters.ClientKey(IPAddress.Parse("2001:db8:1:2:1:2:3:4"));
        var otherNetwork = ClientLimiters.ClientKey(IPAddress.Parse("2001:db8:1:3:aaaa:bbbb:cccc:dddd"));

        Assert.Equal(a, sameNetwork); // rotating through the addresses of one /64 does not give a fresh allowance
        Assert.NotEqual(a, otherNetwork);
    }
}
