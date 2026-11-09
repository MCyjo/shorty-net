namespace Shorty.Api.RateLimiting;

public enum LimitAlgorithm
{
    /// <summary>Counts requests in consecutive windows. Cheapest, but allows up to twice the limit across a window boundary.</summary>
    FixedWindow,

    /// <summary>Counts requests in a window that slides in segments. No boundary burst; costs a little more memory.</summary>
    SlidingWindow,

    /// <summary>A bucket refilled at a steady rate. Allows a burst up to the bucket size, then the steady rate.</summary>
    TokenBucket,
}

/// <summary>One limit: <see cref="Limit"/> requests per <see cref="Window"/>; how that is enforced depends on the algorithm.</summary>
public sealed class LimiterSettings
{
    public LimitAlgorithm Algorithm { get; set; } = LimitAlgorithm.SlidingWindow;

    /// <summary>Requests allowed per window; for a token bucket also the bucket size (the largest burst).</summary>
    public int Limit { get; set; } = 100;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Sliding window only: how many segments the window is cut into. More segments, smoother, more memory.</summary>
    public int Segments { get; set; } = 10;
}

public sealed class RateLimitSettings
{
    public const string Section = "RateLimiting";

    /// <summary>Switch everything off, for example for a load test generated from one address.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Believe <c>X-Forwarded-For</c> when working out who the client is. Leave off unless the application is only reachable
    /// through a proxy that sets it: otherwise any client can pick its own address and never be limited.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    // Starting points, not measured values: to be reviewed against the load tests.

    /// <summary>Creating links: a person or a script. A burst is fine, a sustained flood is not.</summary>
    public LimiterSettings Create { get; set; } = new() { Algorithm = LimitAlgorithm.TokenBucket, Limit = 20, Window = TimeSpan.FromMinutes(1) };

    /// <summary>Following links: public and busy. The limit stops one client from scanning codes or hammering the cache path.</summary>
    public LimiterSettings Redirect { get; set; } = new() { Algorithm = LimitAlgorithm.SlidingWindow, Limit = 600, Window = TimeSpan.FromMinutes(1) };
}
