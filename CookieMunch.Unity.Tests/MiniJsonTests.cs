using CookieMunch;
using Xunit;

namespace CookieMunch.Tests;

/// <summary>
/// The reader that turns a server response into a decision. It is hand-written because
/// Unity has no dependency-free JSON parser that also runs here, so it gets tested against
/// the things that break naive extraction — not just the happy path.
/// </summary>
public class MiniJsonTests
{
    private const string Config = """
        {"cbid":"cb-1","region":"us-ca","regulation":{
          "region":"us-ca","class":"us",
          "regulations":{"gdprApplies":false,"ccpaApplies":true,"lgpdApplies":false},
          "model":"opt-out","defaultState":"granted","framework":"gpp",
          "forcedOptOut":true,"consentRequired":false}}
        """;

    [Fact]
    public void ReadsTopLevelStrings()
    {
        Assert.Equal("cb-1", MiniJson.GetString(Config, "cbid"));
        Assert.Equal("us-ca", MiniJson.GetString(Config, "region"));
    }

    [Fact]
    public void ReadsANestedObjectWhole()
    {
        var reg = MiniJson.GetObject(Config, "regulation");
        Assert.NotNull(reg);
        Assert.Equal("opt-out", MiniJson.GetString(reg!, "model"));
        Assert.True(MiniJson.GetBool(reg!, "forcedOptOut"));
        Assert.False(MiniJson.GetBool(reg!, "consentRequired"));
    }

    /// <summary>
    /// `region` appears at the top level AND inside `regulation`. A search for the key text
    /// would find whichever came first in the string; walking the document finds the one
    /// that is actually a member of the object you asked about.
    /// </summary>
    [Fact]
    public void DoesNotConfuseAKeyWithTheSameNameInsideANestedObject()
    {
        var reg = MiniJson.GetObject(Config, "regulation")!;
        Assert.Equal("us-ca", MiniJson.GetString(reg, "region"));
        // The nested `regulations` object also has no `model`; the outer one does not either.
        Assert.Null(MiniJson.GetString(Config, "model"));
        Assert.Null(MiniJson.GetString(Config, "class"));
    }

    /// <summary>A key name sitting inside a string VALUE must not be matched.</summary>
    [Fact]
    public void DoesNotMatchAKeyNameThatAppearsInsideAStringValue()
    {
        const string json = """{"note":"\"model\":\"opt-in\"","model":"opt-out"}""";
        Assert.Equal("opt-out", MiniJson.GetString(json, "model"));
    }

    [Fact]
    public void UnescapesStringValues()
    {
        const string json = """{"stamp":"a\"b\\c\nd\u0041"}""";
        Assert.Equal("a\"b\\c\ndA", MiniJson.GetString(json, "stamp"));
    }

    /// <summary>A brace inside a string must not unbalance the object scan.</summary>
    [Fact]
    public void HandlesBracesInsideStringValues()
    {
        const string json = """{"a":{"note":"} not the end {"},"b":"after"}""";
        Assert.Equal("after", MiniJson.GetString(json, "b"));
        Assert.Equal("} not the end {", MiniJson.GetString(MiniJson.GetObject(json, "a")!, "note"));
    }

    [Fact]
    public void ReadsNumbersIncludingNegativeAndLarge()
    {
        const string json = """{"ver":2,"utc":1700000000000,"offset":-5}""";
        Assert.Equal(2, MiniJson.GetInt(json, "ver"));
        Assert.Equal(1700000000000L, MiniJson.GetLong(json, "utc"));
        Assert.Equal(-5, MiniJson.GetInt(json, "offset"));
    }

    [Fact]
    public void SkipsArraysWithoutLosingItsPlace()
    {
        const string json = """{"list":[1,2,{"x":"}"}],"after":"yes"}""";
        Assert.Equal("yes", MiniJson.GetString(json, "after"));
    }

    /// <summary>
    /// Every accessor returns null rather than throwing. A malformed response must leave the
    /// caller on its locally-resolved answer, not crash the game.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("""{"a":"unterminated""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void MalformedInputYieldsNullRatherThanThrowing(string json)
    {
        Assert.Null(MiniJson.GetString(json, "a"));
        Assert.Null(MiniJson.GetBool(json, "a"));
        Assert.Null(MiniJson.GetObject(json, "a"));
        Assert.Null(MiniJson.GetInt(json, "a"));
    }

    [Fact]
    public void AnAbsentKeyIsNull()
    {
        Assert.Null(MiniJson.GetString(Config, "nope"));
        Assert.Null(MiniJson.GetBool(Config, "nope"));
    }

    [Fact]
    public void ToleratesWhitespaceAnywhere()
    {
        const string json = "{ \n \"a\" :  \"x\" , \"b\"\t:\ttrue }";
        Assert.Equal("x", MiniJson.GetString(json, "a"));
        Assert.True(MiniJson.GetBool(json, "b"));
    }
}
