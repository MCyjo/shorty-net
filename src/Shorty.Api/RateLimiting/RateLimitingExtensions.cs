using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Shorty.Core;

namespace Shorty.Api.RateLimiting;

internal static class RateLimitPolicies
{
    public const string Create = "create";
    public const string Redirect = "redirect";
}

internal static class RateLimitingExtensions
{
    public static void AddShortyRateLimiting(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<RateLimitSettings>(builder.Configuration.GetSection(RateLimitSettings.Section));
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitSettings>>((limiter, options) =>
        {
            var settings = options.Value;
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = static (context, _) =>
            {
                var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "unknown";
                ShortyMetrics.RateLimited.Add(1, new KeyValuePair<string, object?>("policy", policy));

                // Tell a well-behaved client when to come back; one second if the limiter cannot say.
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : 1;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };

            limiter.AddPolicy(RateLimitPolicies.Create, http => Partition(http, settings, settings.Create));
            limiter.AddPolicy(RateLimitPolicies.Redirect, http => Partition(http, settings, settings.Redirect));
        });

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // The proxy in front (for example the Container Apps ingress) has no fixed address, so none can be listed. This is
            // why the setting is off by default. With ForwardLimit 1 only the entry the nearest proxy appended is used; any
            // address the client put in the header itself is ignored.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }

    public static void UseShortyRateLimiting(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<RateLimitSettings>>().Value.TrustForwardedHeaders)
            app.UseForwardedHeaders();
        app.UseRateLimiter();
    }

    private static RateLimitPartition<string> Partition(HttpContext http, RateLimitSettings all, LimiterSettings limit) =>
        all.Enabled
            ? ClientLimiters.For(ClientLimiters.ClientKey(http.Connection.RemoteIpAddress), limit)
            : RateLimitPartition.GetNoLimiter("off");
}
