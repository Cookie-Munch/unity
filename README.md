# Cookie Munch — Unity SDK

Consent management for Unity games: GDPR, CCPA and LGPD. It tells you **whether you need
to prompt at all**, gates your ad and analytics SDKs until consent is granted, and logs
every decision to your self-hosted Cookie Munch server.

Same consent record, same categories and the same jurisdiction table as the web embed and
the iOS, Android, Flutter and React Native SDKs — a decision made in your game and one made
in a browser are the same row in your ledger.

**Unity 2021.3+.** No third-party dependencies.

## Install

Package Manager → *Add package from git URL*:

```
https://github.com/Cookie-Munch/unity.git
```

## Start

```csharp
using CookieMunch;

var consent = CookieMunchUnity.Create("cb-your-site-id", "https://cmp.example.com");
consent.Load();

// The device's locale says where it was SOLD. This asks the server, which sees the IP.
await consent.RefreshRegulationAsync();

if (consent.IsConsentRequired)
{
    ShowMyConsentScreen();   // your own UI — this SDK does not draw one
}
```

`IsConsentRequired` is `false` once the player has answered, and `false` when a
platform-level opt-out signal has already answered for them. A game that re-prompts on
every cold start trains players to dismiss the prompt without reading it.

## Gating your ad SDK

```csharp
consent.Gate(ConsentCategory.Marketing, () => MobileAds.Initialize());
consent.Gate(ConsentCategory.Statistics, () => Analytics.StartSession());
```

The closure runs the moment the category is granted, and never runs if it is not. This is
the point of a CMP: initialising an ads SDK and hoping to stop it later is not
prior-blocking — by then it has opened a connection and read an advertising id.

## Collecting the decision

```csharp
await consent.AcceptAsync();                       // grant everything
await consent.DeclineAsync();                      // refuse everything but necessary
await consent.SubmitAsync(prefs, stats, marketing); // a specific set
await consent.SetAsync(ConsentCategory.Marketing, false);

consent.OnChange(state => Debug.Log($"marketing: {state.Marketing}"));
```

Every decision is persisted to `PlayerPrefs` **before** the network call, and the sync
never throws back into your code. A consent decision is never lost to a bad connection.

## Which law applies

```csharp
var reg = consent.ApplicableRegulation;
if (reg.CcpaApplies && reg.Model == ConsentModel.OptOut) ShowDoNotSellButton();
```

| Field | Meaning |
|---|---|
| `Region` | ISO 3166-1 alpha-2, optionally with a subdivision (`us-ca`) |
| `Class` | `Eu` \| `Us` \| `Br` \| `Ca` \| `Other` |
| `GdprApplies`, `CcpaApplies`, `LgpdApplies` | Which law is in play |
| `Model` | `OptIn` (ask first) or `OptOut` (fire, but honour a refusal) |
| `DefaultGranted` | What categories default to before a decision |
| `Framework` | `Tcf`, `Gpp` or `None` |
| `ForcedOptOut` | A GPC or DNT signal already refused for them |
| `ConsentRequired` | A decision still has to be collected |

If your game surfaces a Global Privacy Control or Do Not Track setting, pass it on with
`SetGlobalPrivacyControl(true)` / `SetDoNotTrack(true)`. Under an opt-out regime that
counts as a refusal and no prompt is owed; under GDPR nothing fires before consent anyway,
so the prompt still is.

## Web views

If your game opens web content, seed it with the decision the player already gave so they
are not asked twice:

```csharp
myWebViewPlugin.EvaluateJavaScript(WebViewBridge.JavaScript(consent.State));
// or, where script evaluation is unavailable:
myWebViewPlugin.Load(WebViewBridge.Url("https://example.com/privacy", consent.State));
```

## Testing

The core is plain C# with no `UnityEngine` references, so it runs under `dotnet test`
without a Unity licence — which is also how CI verifies it:

```bash
dotnet test native/unity/CookieMunch.Unity.Tests/CookieMunch.Unity.Tests.csproj
```

Inject `IConsentStorage` and `IConsentTransport` to test your own consent flow with no
network and no PlayerPrefs:

```csharp
var consent = new CookieMunchConsent("cb-1", "https://cmp.example.com",
    new InMemoryConsentStorage(), new NullConsentTransport(), region: "de");
```
