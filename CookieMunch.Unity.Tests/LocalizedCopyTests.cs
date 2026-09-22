using CookieMunch;
using Xunit;

namespace CookieMunch.Unity.Tests;

/// <summary>
/// The prompt's words arrive with the config, in the player's language. A build cannot carry
/// forty catalogues, and five SDKs each carrying their own is five chances to disagree about
/// what one prompt says.
/// </summary>
public class LocalizedCopyTests
{
    private const string Body = """
    {
      "regulation": null,
      "copy": {
        "language": "ko",
        "rtl": false,
        "banner": { "title": "개인정보를 소중히 다룹니다", "acceptAll": "모두 허용", "rejectAll": "모두 거부" },
        "categories": { "marketing": { "label": "마케팅", "description": "설명" } },
        "reopen": "쿠키 설정"
      }
    }
    """;

    [Fact]
    public void ReadsTheServersCopy()
    {
        var copy = LocalizedCopy.FromConfigJson(Body);
        Assert.NotNull(copy);
        Assert.Equal("ko", copy!.Language);
        Assert.False(copy.Rtl);
        Assert.Equal("모두 허용", copy.AcceptAll);
        Assert.Equal("마케팅", copy.Categories["marketing"].Label);
        Assert.Equal("쿠키 설정", copy.Reopen);
    }

    [Fact]
    public void AResponseWithoutCopyIsNotAnError()
    {
        Assert.Null(LocalizedCopy.FromConfigJson("{\"regulation\":null}"));
    }

    /// <summary>A damaged body must never break a prompt; it falls back to English.</summary>
    [Fact]
    public void AMalformedBodyIsNoCopy()
    {
        Assert.Null(LocalizedCopy.FromConfigJson("{not json"));
        Assert.Null(LocalizedCopy.FromConfigJson(string.Empty));
    }

    [Fact]
    public void MissingFieldsAreNullRatherThanEmptyStrings()
    {
        var copy = LocalizedCopy.FromConfigJson("{\"copy\":{\"language\":\"en\",\"rtl\":false,\"banner\":{},\"categories\":{},\"reopen\":\"\"}}");
        Assert.NotNull(copy);
        Assert.Null(copy!.Title);
        Assert.Null(copy.AcceptAll);
        Assert.Null(copy.Reopen);
        Assert.Empty(copy.Categories);
    }

    [Fact]
    public void MarksARightToLeftLanguage()
    {
        var copy = LocalizedCopy.FromConfigJson("{\"copy\":{\"language\":\"ar\",\"rtl\":true,\"banner\":{},\"categories\":{},\"reopen\":\"x\"}}");
        Assert.True(copy!.Rtl);
    }
}
