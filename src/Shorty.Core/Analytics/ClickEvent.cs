namespace Shorty.Core.Analytics;

/// <summary>
/// One redirect that was served. <see cref="EventId"/> makes delivery safe to repeat: the broker delivers at least once, so
/// the consumer stores each id once. No address and no user agent is kept, only the host the visitor came from.
/// </summary>
public sealed record ClickEvent(Guid EventId, string Code, DateTimeOffset ClickedAt, string? ReferrerHost);
