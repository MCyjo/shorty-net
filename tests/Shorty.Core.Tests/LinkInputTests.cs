using Shorty.Core;

namespace Shorty.Core.Tests;

public class LinkInputTests
{
    [Theory]
    [InlineData("https://example.com/very/long/url?x=1#top", "https://example.com/very/long/url?x=1#top")]
    [InlineData("  http://example.com  ", "http://example.com/")]
    [InlineData("HTTPS://Example.COM/Path", "https://example.com/Path")]
    public void ValidUrls_AreAcceptedInCanonicalForm(string input, string expected)
    {
        Assert.True(LinkInput.TryNormalizeUrl(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/file")]
    [InlineData("https://trusted.example@evil.example/login")]  // credentials: the classic look-alike trick
    [InlineData("https://user:password@example.com")]
    public void UnsafeOrMalformedUrls_AreRejected(string? input)
    {
        Assert.False(LinkInput.TryNormalizeUrl(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ATooLongUrl_IsRejected()
    {
        var url = "https://example.com/" + new string('a', LinkInput.MaxUrlLength);

        Assert.False(LinkInput.TryNormalizeUrl(url, out _, out _));
    }

    [Theory]
    [InlineData("docs")]
    [InlineData("My-Blog_2026")]
    [InlineData("abc")]
    public void ValidAliases_AreAccepted(string alias) =>
        Assert.True(LinkInput.TryValidateAlias(alias, out _));

    [Theory]
    [InlineData("ab")]                    // too short
    [InlineData("has space")]
    [InlineData("slash/inside")]
    [InlineData("dot.dot")]
    [InlineData("ünïcode")]
    [InlineData("api")]                   // would be shadowed by /api/...
    [InlineData("API")]
    [InlineData("health")]
    public void InvalidOrReservedAliases_AreRejected(string alias) =>
        Assert.False(LinkInput.TryValidateAlias(alias, out _));

    [Fact]
    public void AnAliasOfThirtyThreeCharacters_IsRejected() =>
        Assert.False(LinkInput.TryValidateAlias(new string('a', 33), out _));
}
