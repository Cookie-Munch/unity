using CookieMunch;
using Xunit;

namespace CookieMunch.Tests;

/// <summary>
/// A game's first question is not "what did they consent to" but "do I have to ask".
/// Getting that wrong in either direction is expensive: prompt a player in Texas under
/// GDPR rules and you have depressed your opt-in rate — and with it your ad revenue — for
/// nothing; skip the prompt for one in Germany and you are not compliant.
///
/// The table here is the same one in packages/geo/src/regulation.ts and the Swift, Kotlin
/// and Dart ports. The cases are deliberately identical so a drift shows up as a failure.
/// </summary>
public class RegulationTests
{
    [Fact]
    public void EuropeIsGdprOptIn()
    {
        var reg = Regulation.Resolve("de");
        Assert.Equal(RegionClass.Eu, reg.Class);
        Assert.True(reg.GdprApplies);
        Assert.False(reg.CcpaApplies);
        Assert.Equal(ConsentModel.OptIn, reg.Model);
        Assert.False(reg.DefaultGranted);
        Assert.Equal(SignalFramework.Tcf, reg.Framework);
    }

    [Theory]
    [InlineData("gb")]
    [InlineData("uk")]
    public void TheUnitedKingdomCountsAsEurope(string region) =>
        Assert.Equal(RegionClass.Eu, Regulation.Resolve(region).Class);

    [Fact]
    public void CaliforniaIsCcpaOptOut()
    {
        var reg = Regulation.Resolve("us-ca");
        Assert.Equal(RegionClass.Us, reg.Class);
        Assert.True(reg.CcpaApplies);
        Assert.Equal(ConsentModel.OptOut, reg.Model);
        Assert.True(reg.DefaultGranted);
        Assert.Equal(SignalFramework.Gpp, reg.Framework);
    }

    [Fact]
    public void BrazilIsLgpd()
    {
        Assert.True(Regulation.Resolve("br").LgpdApplies);
        Assert.Equal(ConsentModel.OptIn, Regulation.Resolve("br").Model);
    }

    [Theory]
    [InlineData("FR", RegionClass.Eu)]
    [InlineData("US-NY", RegionClass.Us)]
    public void RegionIsCaseInsensitiveAndTolerantOfSubdivisions(string region, RegionClass expected) =>
        Assert.Equal(expected, Regulation.Resolve(region).Class);

    /// <summary>A geo lookup that fails must never silently downgrade someone's protections.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("unknown")]
    public void UnknownRegionFallsBackToOptIn(string region)
    {
        var reg = Regulation.Resolve(region);
        Assert.Equal(ConsentModel.OptIn, reg.Model);
        Assert.False(reg.DefaultGranted);
    }

    [Fact]
    public void GpcForcesOptOutOnlyWhereCollectionWouldOtherwiseProceed()
    {
        Assert.True(Regulation.Resolve("us-ca", gpc: true).ForcedOptOut);
        // Under GDPR nothing fires before consent, so there is nothing for GPC to stop.
        Assert.False(Regulation.Resolve("de", gpc: true).ForcedOptOut);
    }

    [Fact]
    public void DoNotTrackForcesOptOutAndCanBeDisabled()
    {
        Assert.True(Regulation.Resolve("us-tx", dnt: true).ForcedOptOut);
        Assert.False(Regulation.Resolve("us-tx", dnt: true, honorDnt: false).ForcedOptOut);
    }

    [Fact]
    public void ConsentRequiredIsFalseOnlyWhenTheySignalledAlready()
    {
        Assert.True(Regulation.Resolve("de").ConsentRequired);
        Assert.True(Regulation.Resolve("us-ca").ConsentRequired);
        Assert.False(Regulation.Resolve("us-ca", gpc: true).ConsentRequired);
    }

    [Fact]
    public void ParsesTheServerPayload()
    {
        var reg = Regulation.FromConfigJson("""
            {"cbid":"x","region":"us-ca","regulation":{
              "region":"us-ca","class":"us",
              "regulations":{"gdprApplies":false,"ccpaApplies":true,"lgpdApplies":false},
              "model":"opt-out","defaultState":"granted","framework":"gpp",
              "forcedOptOut":true,"consentRequired":false}}
            """)!;
        Assert.Equal("us-ca", reg.Region);
        Assert.Equal(RegionClass.Us, reg.Class);
        Assert.True(reg.CcpaApplies);
        Assert.Equal(ConsentModel.OptOut, reg.Model);
        Assert.True(reg.DefaultGranted);
        Assert.True(reg.ForcedOptOut);
        Assert.False(reg.ConsentRequired);
    }

    /// <summary>A server that learns a new jurisdiction tomorrow must not crash a build shipped today.</summary>
    [Fact]
    public void UnrecognisedClassParsesAsOtherRatherThanThrowing()
    {
        var reg = Regulation.FromConfigJson("""
            {"regulation":{"region":"jp","class":"apac","regulations":{},
             "model":"opt-in","defaultState":"denied","framework":"none",
             "forcedOptOut":false,"consentRequired":true}}
            """)!;
        Assert.Equal(RegionClass.Other, reg.Class);
        Assert.Equal(ConsentModel.OptIn, reg.Model);
    }

    /// <summary>
    /// An older server omits consentRequired entirely. Defaulting a missing bool to false
    /// would suppress every prompt on the planet, so it is derived instead.
    /// </summary>
    [Fact]
    public void AMissingConsentRequiredIsDerivedNotDefaultedToFalse()
    {
        var reg = Regulation.FromConfigJson("""
            {"regulation":{"region":"de","class":"eu","regulations":{"gdprApplies":true},
             "model":"opt-in","defaultState":"denied","framework":"tcf","forcedOptOut":false}}
            """)!;
        Assert.True(reg.ConsentRequired);
    }

    [Theory]
    [InlineData("""{"cbid":"x"}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void AbsentOrMalformedPayloadsParseToNull(string body) =>
        Assert.Null(Regulation.FromConfigJson(body));
}
