using System.Text.Json;
using System.Text.Json.Nodes;
using CookieMunch;
using Xunit;

namespace CookieMunch.Tests;

/// <summary>
/// Carrying a decision from the game into a web view. The contract that matters is that
/// the WEB READER recovers the right decision — see
/// packages/core/test/webview-crossplatform.test.ts, which runs this SDK's real output
/// through it.
/// </summary>
public class WebViewBridgeTests
{
    private static ConsentState State(string stamp = "abc-123") =>
        new(true, false, true, ConsentMethod.Explicit, stamp, 1, 1_700_000_000_000, "de");

    /// <summary>Undo the cookie layer, the way the embed's parseValue does.</summary>
    private static JsonNode ReadBack(string encoded) =>
        JsonNode.Parse(System.Uri.UnescapeDataString(encoded))!;

    /// <summary>Undo BOTH layers, the way parseWebViewConsent does.</summary>
    private static JsonNode ReadBackFromQuery(string raw) => ReadBack(System.Uri.UnescapeDataString(raw));

    private static string CookieValue(string js) =>
        System.Text.RegularExpressions.Regex.Match(js, @"'CookieMunch='\+'([^']*)'").Groups[1].Value;

    [Fact]
    public void WritesTheCookieTheEmbedReadsCarryingEveryField()
    {
        var js = WebViewBridge.JavaScript(State());
        Assert.StartsWith("document.cookie='CookieMunch='", js);
        Assert.Contains("path=/", js);
        Assert.Contains($"max-age={WebViewBridge.DefaultMaxAge}", js);
        Assert.Contains("SameSite=Lax", js);

        var decoded = ReadBack(CookieValue(js));
        Assert.True(decoded["preferences"]!.GetValue<bool>());
        Assert.False(decoded["statistics"]!.GetValue<bool>());
        Assert.True(decoded["marketing"]!.GetValue<bool>());
        Assert.True(decoded["necessary"]!.GetValue<bool>());
        Assert.Equal("explicit", decoded["method"]!.GetValue<string>());
        Assert.Equal("abc-123", decoded["stamp"]!.GetValue<string>());
        Assert.Equal("de", decoded["region"]!.GetValue<string>());
    }

    /// <summary>
    /// Every plugin's evaluator takes a single string; a multi-line program is a common
    /// source of silent cross-platform failures.
    /// </summary>
    [Fact]
    public void IsASingleLine() => Assert.DoesNotContain("\n", WebViewBridge.JavaScript(State()));

    [Fact]
    public void HonoursACustomLifetime() =>
        Assert.Contains("max-age=60", WebViewBridge.JavaScript(State(), maxAge: 60));

    [Fact]
    public void CanAlsoWriteTheLegacyCookieForAMigratingGame()
    {
        var js = WebViewBridge.JavaScript(State(), alsoLegacyCookie: true);
        Assert.Contains("'CookieMunch='", js);
        Assert.Contains("'CookieConsent='", js);
        // Two complete statements, not one malformed concatenation.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(js, "document\\.cookie=").Count);
    }

    /// <summary>
    /// The value is JSON inside a single-quoted JS literal. A quote that escaped unescaped
    /// would terminate the literal early and evaluate the rest of the stamp as code.
    /// </summary>
    [Fact]
    public void EscapesAnythingThatCouldBreakOutOfTheLiteral()
    {
        var js = WebViewBridge.JavaScript(State("'; alert(1); //</script><x y='"));

        Assert.DoesNotContain("</script", js);
        // Every quote is either one of the three literal pairs or backslash-escaped.
        Assert.Equal(6, js.Replace("\\'", string.Empty).Split('\'').Length - 1);
    }

    [Fact]
    public void TheQueryStringRoundTripsThroughAWebStyleDecode()
    {
        var qs = WebViewBridge.QueryString(State());
        Assert.StartsWith($"{WebViewBridge.Param}=", qs);
        var decoded = ReadBackFromQuery(qs.Substring(WebViewBridge.Param.Length + 1));
        Assert.Equal("abc-123", decoded["stamp"]!.GetValue<string>());
        Assert.True(decoded["marketing"]!.GetValue<bool>());
    }

    /// <summary>
    /// Single-encoding round-trips for simple values and then corrupts the first decision
    /// containing a '%'. The Dart port shipped with exactly that bug for one commit.
    /// </summary>
    [Fact]
    public void SurvivesAValueContainingALiteralPercentSign()
    {
        var qs = WebViewBridge.QueryString(State("100%-sure"));
        var decoded = ReadBackFromQuery(qs.Substring(WebViewBridge.Param.Length + 1));
        Assert.Equal("100%-sure", decoded["stamp"]!.GetValue<string>());
    }

    [Fact]
    public void UrlPreservesAQueryTheCallerAlreadyHad()
    {
        var url = WebViewBridge.Url("https://example.com/help?topic=billing", State());
        Assert.Contains("topic=billing", url);
        Assert.Contains($"{WebViewBridge.Param}=", url);
    }

    /// <summary>
    /// A web view that reloads its own URL would otherwise accumulate parameters until the
    /// request line was too long to send.
    /// </summary>
    [Fact]
    public void UrlReplacesRatherThanDuplicatingOnASecondPass()
    {
        var url = WebViewBridge.Url(WebViewBridge.Url("https://example.com/", State()), State());
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(url, $"{WebViewBridge.Param}="));
    }

    [Fact]
    public void UrlKeepsTheFragmentAfterTheQuery()
    {
        var url = WebViewBridge.Url("https://example.com/help#section", State());
        Assert.EndsWith("#section", url);
        Assert.Contains($"?{WebViewBridge.Param}=", url);
    }

    /// <summary>
    /// Neither Uri.EscapeDataString nor UnityWebRequest.EscapeURL matches
    /// encodeURIComponent, and either difference changes what the web reader gets back.
    /// </summary>
    [Fact]
    public void EncodesExactlyLikeEncodeUriComponent()
    {
        Assert.Equal("-_.!~*'()", WebViewBridge.EncodeComponent("-_.!~*'()"));
        Assert.Equal("%20", WebViewBridge.EncodeComponent(" "));
        Assert.Equal("%7B%22a%22%3A1%7D", WebViewBridge.EncodeComponent("{\"a\":1}"));
        Assert.Equal("%E2%82%AC", WebViewBridge.EncodeComponent("€"));
    }
}
