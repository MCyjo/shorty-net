using Shorty.Api.Persistence;
using Shorty.Core;

namespace Shorty.Api.Links;

public sealed record CreateLinkRequest(string? Url, string? Alias, DateTimeOffset? ExpiresAt);

public sealed record CreateLinkResponse(string Code, string ShortUrl, string Url, DateTimeOffset? ExpiresAt);

internal static class LinkEndpoints
{
    public static void MapLinkEndpoints(this WebApplication app)
    {
        app.MapPost("/api/links", CreateAsync);

        // One path segment, so /health/live and /api/links never match; "api" and "health" are reserved as aliases.
        // Redirects are temporary (302): a 301 is cached by browsers for good, which would hide every later click
        // from analytics and make a changed or expired link impossible to retract.
        app.MapGet("/{code:regex(^[A-Za-z0-9_-]{{3,32}}$)}", RedirectAsync);
    }

    private static async Task<IResult> CreateAsync(
        CreateLinkRequest request, HttpRequest http, LinkService links, TimeProvider clock, IConfiguration config, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();

        if (!LinkInput.TryNormalizeUrl(request.Url, out var url, out var urlError))
            errors["url"] = [urlError!];
        if (request.Alias is not null && !LinkInput.TryValidateAlias(request.Alias, out var aliasError))
            errors["alias"] = [aliasError!];
        if (request.ExpiresAt is { } expiresAt && expiresAt <= clock.GetUtcNow())
            errors["expiresAt"] = ["Must be in the future."];

        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var outcome = await links.CreateAsync(url, request.Alias, request.ExpiresAt, ct);
        if (outcome.Status == CreateStatus.AliasTaken)
            return Results.Conflict(new { error = $"The alias '{request.Alias}' is already taken." });

        var link = outcome.Link!;
        var baseUrl = (config["Shorty:BaseUrl"] ?? $"{http.Scheme}://{http.Host}").TrimEnd('/');
        var shortUrl = $"{baseUrl}/{link.Code}";
        return Results.Created(shortUrl, new CreateLinkResponse(link.Code, shortUrl, link.Url, link.ExpiresAt));
    }

    private static async Task<IResult> RedirectAsync(string code, ILinkStore store, TimeProvider clock, CancellationToken ct)
    {
        var link = await store.FindAsync(code, ct);
        if (link is null) return Results.NotFound();
        if (link.ExpiresAt is { } expiresAt && expiresAt <= clock.GetUtcNow()) return Results.StatusCode(StatusCodes.Status410Gone);

        return Results.Redirect(link.Url, permanent: false);
    }
}
