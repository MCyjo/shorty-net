using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Shorty.Api.Persistence;
using Shorty.Core;

namespace Shorty.Api.Caching;

public sealed class CacheSettings
{
    public const string Section = "Cache";

    /// <summary>How long a link stays in Redis (the shared layer).</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long an instance keeps a link in its own memory. This also bounds how stale a change can be on the other
    /// instances, because a removal reaches Redis and the local memory of the instance that made it, not of the others.
    /// </summary>
    public TimeSpan LocalTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long "this code does not exist" is remembered: short, so a new link with that name appears quickly.</summary>
    public TimeSpan NegativeTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Random spread (±) applied to <see cref="Ttl"/>, so that entries created together do not all expire together.</summary>
    public double Jitter { get; set; } = 0.1;
}

/// <summary>What is cached for a code. <see cref="Url"/> is null for a code that does not exist (negative caching).</summary>
internal sealed record CachedLink(string? Url, DateTimeOffset? ExpiresAt)
{
    public static readonly CachedLink Missing = new(null, null);

    public bool Found => Url is not null;
}

/// <summary>
/// Cache-aside for redirects: look in memory, then in Redis, then in PostgreSQL, and fill the layers on the way back.
/// Concurrent requests for a key that is not cached yet share one database read instead of each making their own.
/// </summary>
internal sealed partial class LinkCache(
    HybridCache cache, ILinkStore store, IOptions<CacheSettings> settings, ILogger<LinkCache> logger)
{
    private static string Key(string code) => $"link:{code}";

    private sealed class Load(string code, ILinkStore store)
    {
        public string Code { get; } = code;
        public ILinkStore Store { get; } = store;
        public bool ReadDatabase { get; set; }
    }

    public async Task<CachedLink> GetAsync(string code, CancellationToken ct)
    {
        var options = settings.Value;
        var load = new Load(code, store);

        var link = await cache.GetOrCreateAsync(
            Key(code),
            load,
            static async (state, token) =>
            {
                state.ReadDatabase = true;
                ShortyMetrics.CacheMisses.Add(1);
                var found = await state.Store.FindAsync(state.Code, token);
                return found is null ? CachedLink.Missing : new CachedLink(found.Url, found.ExpiresAt);
            },
            new HybridCacheEntryOptions { Expiration = WithJitter(options.Ttl, options.Jitter), LocalCacheExpiration = options.LocalTtl },
            cancellationToken: ct);

        // A code that does not exist was just stored with the long lifetime of a real link. Shorten it: if the entry is
        // overwritten with the short one, a link created under that name shows up within seconds, not within an hour.
        if (!link.Found && load.ReadDatabase)
        {
            await cache.SetAsync(
                Key(code), link,
                new HybridCacheEntryOptions { Expiration = options.NegativeTtl, LocalCacheExpiration = options.NegativeTtl },
                cancellationToken: ct);
        }

        return link;
    }

    /// <summary>Forgets a code. Best effort: a link was just created, and a cache outage must not turn that into an error.</summary>
    public async Task InvalidateAsync(string code, CancellationToken ct)
    {
        try
        {
            await cache.RemoveAsync(Key(code), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // While Redis is down this fails on every created link; one line every half minute is enough to notice it.
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastInvalidationWarning);
            if (now - last >= 30_000 && Interlocked.CompareExchange(ref _lastInvalidationWarning, now, last) == last)
                LogInvalidationFailed(ex.GetType().Name, code);
        }
    }

    private long _lastInvalidationWarning = long.MinValue / 2;

    private static TimeSpan WithJitter(TimeSpan ttl, double jitter) =>
        ttl * (1 + ((Random.Shared.NextDouble() * 2) - 1) * jitter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not invalidate the cache entry for {Code} ({Error}); it will expire on its own. Logged at most every 30 seconds.")]
    private partial void LogInvalidationFailed(string error, string code);
}
