using System.Threading.Tasks;
using CookieMunch;
using Xunit;

namespace CookieMunch.Tests;

/// <summary>
/// Linking a decision to a signed-in account, so one player's consent correlates across
/// web, mobile and desktop. The server has always accepted subjectId — validated, stored
/// and bound into the hash chain — but only the React Native client ever sent it, and only
/// as a constructor argument, which is close to useless: a game builds its consent client
/// at launch, before anyone has signed in.
/// </summary>
public class SubjectIdTests
{
    private static CookieMunchConsent Client(SpyTransport transport, string? subjectId = null, IConsentStorage? storage = null) =>
        new(
            "cb-1",
            "https://cmp.example.com/",
            storage ?? new InMemoryConsentStorage(),
            transport,
            "de",
            subjectId: subjectId,
            now: () => 1_700_000_000_000,
            stamp: () => "fixed");

    private static string? SentSubjectId(SpyTransport t) =>
        MiniJson.GetString(t.Posts[^1].Body, "subjectId");

    [Fact]
    public async Task NoSubjectIdIsSentWhenNoneIsSet()
    {
        var t = new SpyTransport();
        await Client(t).AcceptAsync();
        Assert.Null(SentSubjectId(t));
        Assert.DoesNotContain("subjectId", t.Posts[^1].Body);
    }

    [Fact]
    public async Task AnIdSetAfterSignInIsAttachedToLaterDecisions()
    {
        var t = new SpyTransport();
        var c = Client(t);

        await c.AcceptAsync();
        Assert.Null(SentSubjectId(t));

        c.SetSubjectId("account-42");
        await c.DeclineAsync();
        Assert.Equal("account-42", SentSubjectId(t));
    }

    [Fact]
    public async Task TheConstructorOptionStillWorks()
    {
        var t = new SpyTransport();
        await Client(t, "account-42").AcceptAsync();
        Assert.Equal("account-42", SentSubjectId(t));
    }

    [Fact]
    public async Task SettingItOverridesTheConstructorValue()
    {
        var t = new SpyTransport();
        var c = Client(t, "from-init");
        c.SetSubjectId("after-sign-in");
        await c.AcceptAsync();
        Assert.Equal("after-sign-in", SentSubjectId(t));
    }

    /// <summary>
    /// Signing out must detach the id. Continuing to send it would attribute the next
    /// player's decisions on a shared device to the account that just left.
    /// </summary>
    [Fact]
    public async Task ClearingItStopsTheIdBeingSent()
    {
        var t = new SpyTransport();
        var c = Client(t, "account-42");
        c.SetSubjectId(null);
        await c.AcceptAsync();
        Assert.Null(SentSubjectId(t));
    }

    [Fact]
    public async Task AnEmptyStringClearsItRatherThanSendingAnEmptyId()
    {
        var t = new SpyTransport();
        var c = Client(t, "account-42");
        c.SetSubjectId("");
        await c.AcceptAsync();
        Assert.Null(SentSubjectId(t));
        Assert.Null(c.SubjectId);
    }

    [Fact]
    public void ItIsReadableBack()
    {
        var c = Client(new SpyTransport());
        Assert.Null(c.SubjectId);
        c.SetSubjectId("account-42");
        Assert.Equal("account-42", c.SubjectId);
    }

    /// <summary>
    /// Who is signed in is the game's business and can change between launches, so a stale
    /// account id baked into a restored record would attribute one player's consent to
    /// another.
    /// </summary>
    [Fact]
    public async Task ItIsNotPersistedWithTheDecision()
    {
        var storage = new InMemoryConsentStorage();
        await Client(new SpyTransport(), "account-42", storage).AcceptAsync();
        Assert.DoesNotContain("account-42", storage.Read("CookieMunch"));
    }

    /// <summary>The sync body stays valid JSON with the extra member appended.</summary>
    [Fact]
    public async Task TheSyncBodyRemainsParseableWithTheIdAttached()
    {
        var t = new SpyTransport();
        var c = Client(t, "account-42");
        await c.SubmitAsync(true, false, true);

        var body = t.Posts[^1].Body;
        Assert.Equal("cb-1", MiniJson.GetString(body, "cbid"));
        Assert.Equal("account-42", MiniJson.GetString(body, "subjectId"));
        Assert.Equal("explicit", MiniJson.GetString(body, "method"));
        Assert.True(MiniJson.GetBool(MiniJson.GetObject(body, "choices")!, "preferences"));
    }
}
