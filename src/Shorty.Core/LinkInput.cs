using System.Text.RegularExpressions;

namespace Shorty.Core;

public static partial class LinkInput
{
    public const int MaxUrlLength = 2048;

    // Paths the application itself serves. A custom alias with one of these names would be shadowed by the route.
    private static readonly HashSet<string> ReservedAliases = new(StringComparer.OrdinalIgnoreCase) { "api", "health" };

    [GeneratedRegex("^[A-Za-z0-9_-]{3,32}$")]
    private static partial Regex AliasPattern();

    /// <summary>Accepts only absolute http(s) URLs without embedded credentials, and returns them in canonical form.</summary>
    public static bool TryNormalizeUrl(string? input, out string normalized, out string? error)
    {
        normalized = string.Empty;
        var trimmed = input?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            error = "A URL is required.";
            return false;
        }
        if (trimmed.Length > MaxUrlLength)
        {
            error = $"The URL is longer than {MaxUrlLength} characters.";
            return false;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            error = "The URL is not a valid absolute URL.";
            return false;
        }
        // An allow-list, not a block-list: javascript:, data:, file:, ftp: and anything else is refused by default.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Only http and https URLs are accepted.";
            return false;
        }
        if (string.IsNullOrEmpty(uri.Host))
        {
            error = "The URL has no host.";
            return false;
        }
        // https://trusted.example@evil.example looks like the first host to a human and goes to the second.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "URLs with embedded credentials are not accepted.";
            return false;
        }

        normalized = uri.AbsoluteUri;
        error = null;
        return true;
    }

    public static bool TryValidateAlias(string alias, out string? error)
    {
        if (!AliasPattern().IsMatch(alias))
        {
            error = "An alias is 3 to 32 characters: letters, digits, '-' and '_'.";
            return false;
        }
        if (ReservedAliases.Contains(alias))
        {
            error = $"'{alias}' is reserved.";
            return false;
        }

        error = null;
        return true;
    }
}
