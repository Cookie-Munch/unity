using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CookieMunch;
using Xunit;

namespace CookieMunch.Tests;

/// <summary>Records every request instead of hitting the network, and can fail on demand.</summary>
internal sealed class SpyTransport : IConsentTransport
{
    private readonly string? _configBody;
    private readonly bool _throwOnPost;

    public SpyTransport(string? configBody = null, bool throwOnPost = false)
    {
        _configBody = configBody;
        _throwOnPost = throwOnPost;
    }

    public List<(string Url, string Region, string Body)> Posts { get; } = new();

    public List<(string Url, string Region)> Gets { get; } = new();

    public Task PostAsync(string url, string region, string jsonBody)
    {
        Posts.Add((url, region, jsonBody));
        if (_throwOnPost) throw new InvalidOperationException("offline");
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string url, string region)
    {
        Gets.Add((url, region));
        if (_configBody == null) throw new InvalidOperationException("offline");
        return Task.FromResult<string?>(_configBody);
    }
}

public class CookieMunchConsentTests
{
    private const string CaliforniaConfig = """
        {"regulation":{"region":"us-ca","class":"us",
         "regulations":{"gdprApplies":false,"ccpaApplies":true,"lgpdApplies":false},
         "model":"opt-out","defaultState":"granted","framework":"gpp",
         "forcedOptOut":false,"consentRequired":true}}
        """;

    private static CookieMunchConsent Client(
        string region = "de",
        IConsentStorage? storage = null,
        IConsentTransport? transport = null) =>
        new(
            "cb-1",
            "https://cmp.example.com/",
            storage ?? new InMemoryConsentStorage(),
            transport ?? new SpyTransport(),
            region,
            now: () => 1_700_000_000_000,
            stamp: () => "fixed-stamp");

    // --- do I prompt ----------------------------------------------------------

    [Fact]
    public void ResolvesTheRegimeOfflineFromTheConfiguredRegion()
    {
        Assert.True(Client("de").ApplicableRegulation.GdprApplies);
        Assert.True(Client("us-ca").ApplicableRegulation.CcpaApplies);
    }

    [Fact]
    public void ConsentIsRequiredBeforeThePlayerHasAnswered() => Assert.True(Client().IsConsentRequired);

    /// <summary>
    /// The single most useful property in the whole API: once they have answered, stop
    /// asking. A game that re-prompts on every cold start trains players to dismiss it.
    /// </summary>
    [Fact]
    public async Task ConsentIsNotRequiredOnceThePlayerHasAnswered()
    {
        var c = Client();
        await c.AcceptAsync();
        Assert.False(c.IsConsentRequired);
    }

    [Fact]
    public async Task DecliningStillCountsAsAnswering()
    {
        var c = Client();
        await c.DeclineAsync();
        Assert.False(c.IsConsentRequired);
    }

    [Fact]
    public void AnOptOutSignalAnswersForThemUnderAnOptOutRegime()
    {
        var c = Client("us-ca");
        Assert.True(c.IsConsentRequired);
        c.SetGlobalPrivacyControl(true);
        Assert.False(c.IsConsentRequired);
        Assert.True(c.ApplicableRegulation.ForcedOptOut);
    }

    /// <summary>GPC is a refusal, not a regime change — a GDPR prompt is still owed.</summary>
    [Fact]
    public void GlobalPrivacyControlDoesNotSuppressAGdprPrompt()
    {
        var c = Client("de");
        c.SetGlobalPrivacyControl(true);
        Assert.True(c.IsConsentRequired);
    }

    [Fact]
    public async Task RefreshAdoptsTheServerAnswerOverTheLocalGuess()
    {
        // A German-locale device, physically in California. The server sees the IP.
        var transport = new SpyTransport(CaliforniaConfig);
        var c = Client("de", transport: transport);
        Assert.True(c.ApplicableRegulation.GdprApplies);

        await c.RefreshRegulationAsync();

        Assert.True(c.ApplicableRegulation.CcpaApplies);
        Assert.False(c.ApplicableRegulation.GdprApplies);
        // The same call asks for the player's language, so the response also carries the
        // prompt's words — a build never ships forty catalogues of its own.
        var (url, sentRegion) = Assert.Single(transport.Gets);
        Assert.StartsWith("https://cmp.example.com/config/cb-1", url);
        Assert.Contains("lang=", url);
        Assert.Equal("de", sentRegion);
    }

    /// <summary>Offline, or a server not yet upgraded. Either way the game keeps an answer.</summary>
    [Fact]
    public async Task RefreshFailureLeavesTheLocalRegulationIntact()
    {
        var c = Client("de", transport: new SpyTransport(null));
        await c.RefreshRegulationAsync();
        Assert.True(c.ApplicableRegulation.GdprApplies);
        Assert.True(c.IsConsentRequired);
    }

    [Fact]
    public async Task RefreshIgnoresAResponseWithNoRegulationBlock()
    {
        var c = Client("de", transport: new SpyTransport("""{"cbid":"cb-1"}"""));
        await c.RefreshRegulationAsync();
        Assert.True(c.ApplicableRegulation.GdprApplies);
    }

    // --- collecting a decision ------------------------------------------------

    [Fact]
    public async Task AcceptGrantsEverythingExplicitly()
    {
        var s = await Client().AcceptAsync();
        Assert.True(s.Preferences && s.Statistics && s.Marketing && s.Necessary);
        Assert.Equal(ConsentMethod.Explicit, s.Method);
    }

    [Fact]
    public async Task DeclineRefusesEverythingButNecessary()
    {
        var s = await Client().DeclineAsync();
        Assert.False(s.Preferences || s.Statistics || s.Marketing);
        Assert.True(s.Necessary);
        Assert.False(s.Consented);
    }

    [Fact]
    public async Task SetChangesOneCategoryAndLeavesTheRest()
    {
        var c = Client();
        await c.SubmitAsync(true, true, true);
        var s = await c.SetAsync(ConsentCategory.Marketing, false);
        Assert.True(s.Preferences);
        Assert.True(s.Statistics);
        Assert.False(s.Marketing);
    }

    [Fact]
    public async Task ADecisionIsPersistedAndRestored()
    {
        var storage = new InMemoryConsentStorage();
        await Client(storage: storage).SubmitAsync(true, false, true);

        var restored = Client(storage: storage).Load();
        Assert.True(restored.Preferences);
        Assert.False(restored.Statistics);
        Assert.True(restored.Marketing);
        Assert.True(restored.HasResponse);
    }

    /// <summary>
    /// A corrupt record must never lock a player out of being asked. It leaves the implied
    /// default in place rather than throwing on a cold start.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("")]
    public void ACorruptStoredRecordLeavesTheImpliedDefault(string raw)
    {
        var storage = new InMemoryConsentStorage();
        storage.Write("CookieMunch", raw);
        var c = Client(storage: storage);
        var s = c.Load();
        Assert.False(s.HasResponse);
        Assert.True(c.IsConsentRequired);
    }

    // --- offline safety -------------------------------------------------------

    /// <summary>The decision is persisted BEFORE the network call and is never lost to it.</summary>
    [Fact]
    public async Task ADecisionSurvivesAFailedSync()
    {
        var storage = new InMemoryConsentStorage();
        var c = Client(storage: storage, transport: new SpyTransport(throwOnPost: true));
        await c.AcceptAsync();

        Assert.True(c.State.Marketing);
        Assert.NotNull(storage.Read("CookieMunch"));
        Assert.True(Client(storage: storage).Load().HasResponse);
    }

    [Fact]
    public async Task TheSyncBodyCarriesTheDecisionAndTheRegionHeader()
    {
        var transport = new SpyTransport();
        await Client("fr", transport: transport).SubmitAsync(true, false, true);

        var post = Assert.Single(transport.Posts);
        Assert.Equal("https://cmp.example.com/api/v1/consent", post.Url);
        Assert.Equal("fr", post.Region);
        Assert.Contains("\"preferences\":true", post.Body);
        Assert.Contains("\"statistics\":false", post.Body);
        Assert.Contains("\"method\":\"explicit\"", post.Body);
    }

    // --- gates ----------------------------------------------------------------

    /// <summary>
    /// The right way to start an ads SDK. Initialising it and hoping to stop it later is not
    /// prior-blocking: by then it has opened a connection and read an advertising id.
    /// </summary>
    [Fact]
    public async Task AGateRunsOnceItsCategoryIsGranted()
    {
        var c = Client();
        var ran = 0;
        c.Gate(ConsentCategory.Marketing, () => ran++);
        Assert.Equal(0, ran);

        await c.AcceptAsync();
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task AGateNeverRunsForACategoryThatWasRefused()
    {
        var c = Client();
        var ran = 0;
        c.Gate(ConsentCategory.Marketing, () => ran++);
        await c.DeclineAsync();
        Assert.Equal(0, ran);
    }

    [Fact]
    public void AGateForAnAlreadyGrantedCategoryRunsImmediately()
    {
        var c = Client();
        var ran = 0;
        c.Gate(ConsentCategory.Necessary, () => ran++);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task ACancelledGateDoesNotRun()
    {
        var c = Client();
        var ran = 0;
        var cancel = c.Gate(ConsentCategory.Marketing, () => ran++);
        cancel();
        await c.AcceptAsync();
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task AGateRunsOnceEvenIfConsentIsCommittedTwice()
    {
        var c = Client();
        var ran = 0;
        c.Gate(ConsentCategory.Statistics, () => ran++);
        await c.AcceptAsync();
        await c.AcceptAsync();
        Assert.Equal(1, ran);
    }

    /// <summary>A gate that throws must not stop the others, or the consent flow.</summary>
    [Fact]
    public async Task AThrowingGateIsIsolated()
    {
        var c = Client();
        var ran = 0;
        c.Gate(ConsentCategory.Marketing, () => throw new InvalidOperationException("boom"));
        c.Gate(ConsentCategory.Marketing, () => ran++);
        await c.AcceptAsync();
        Assert.Equal(1, ran);
        Assert.True(c.State.Marketing);
    }

    // --- change notification --------------------------------------------------

    [Fact]
    public async Task ListenersSeeEveryDecision()
    {
        var c = Client();
        var seen = new List<bool>();
        c.OnChange(s => seen.Add(s.Marketing));
        await c.AcceptAsync();
        await c.DeclineAsync();
        Assert.Equal(new[] { true, false }, seen);
    }

    [Fact]
    public async Task UnsubscribingStopsTheCallbacks()
    {
        var c = Client();
        var count = 0;
        var off = c.OnChange(_ => count++);
        await c.AcceptAsync();
        off();
        await c.DeclineAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AThrowingListenerDoesNotBreakTheConsentFlow()
    {
        var c = Client();
        var reached = false;
        c.OnChange(_ => throw new InvalidOperationException("boom"));
        c.OnChange(_ => reached = true);
        await c.AcceptAsync();
        Assert.True(reached);
        Assert.True(c.State.HasResponse);
    }
}
