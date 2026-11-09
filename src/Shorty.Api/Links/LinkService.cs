using Shorty.Api.Persistence;
using Shorty.Core;

namespace Shorty.Api.Links;

internal enum CreateStatus { Created, AliasTaken }

internal sealed record CreateLinkOutcome(CreateStatus Status, LinkRecord? Link);

internal sealed class LinkService(BlockIdSource ids, ShortCodeGenerator codes, ILinkStore store)
{
    // With ids that are unique by construction a generated code can only collide with a custom alias someone chose
    // earlier, which is rare; a handful of attempts is plenty and an endless loop would hide a real bug.
    private const int MaxAttempts = 5;

    public async Task<CreateLinkOutcome> CreateAsync(string url, string? alias, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        if (alias is not null)
        {
            // A custom alias still gets an internal id (the primary key); only the code is chosen by the caller.
            var customId = await ids.NextIdAsync(ct);
            var custom = new LinkRecord(customId, alias, url, expiresAt);

            if (await store.InsertAsync(custom, isCustom: true, ct) == InsertResult.CodeTaken)
                return new CreateLinkOutcome(CreateStatus.AliasTaken, null);

            ShortyMetrics.LinksCreated.Add(1);
            return new CreateLinkOutcome(CreateStatus.Created, custom);
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var id = await ids.NextIdAsync(ct);
            var link = new LinkRecord(id, codes.FromId(id), url, expiresAt);

            if (await store.InsertAsync(link, isCustom: false, ct) == InsertResult.Inserted)
            {
                ShortyMetrics.LinksCreated.Add(1);
                return new CreateLinkOutcome(CreateStatus.Created, link);
            }

            ShortyMetrics.CodeCollisions.Add(1);
        }

        throw new InvalidOperationException($"No free code after {MaxAttempts} attempts.");
    }
}
