using CookieMunch;
using Xunit;
using Xunit.Abstractions;

namespace CookieMunch.Tests;

/// <summary>
/// Prints the bridge output for a fixed state so it can be captured into
/// packages/core/test/fixtures-unity-bridge.json and run through the real web reader.
/// A page must not be able to tell which platform seeded it.
/// </summary>
public class CrossPlatformFixture
{
    private readonly ITestOutputHelper _output;

    public CrossPlatformFixture(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PrintFixture()
    {
        var state = new ConsentState(true, false, true, ConsentMethod.Explicit, "abc-123", 1, 1_700_000_000_000, "de");
        _output.WriteLine("UNITY_JS>>>" + WebViewBridge.JavaScript(state) + "<<<");
        _output.WriteLine("UNITY_QS>>>" + WebViewBridge.QueryString(state) + "<<<");
        Assert.True(true);
    }
}
