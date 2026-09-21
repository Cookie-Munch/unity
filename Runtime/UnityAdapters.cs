#if UNITY_5_3_OR_NEWER
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace CookieMunch
{
    /// <summary>
    /// Persists the decision in <c>PlayerPrefs</c>. Excluded from the test build (the
    /// UNITY_5_3_OR_NEWER guard), because it is a two-line wrapper over Unity's own API and
    /// there is nothing here a test outside Unity could prove.
    /// <para>
    /// <c>PlayerPrefs.Save()</c> is called on every write. Unity flushes on a clean quit,
    /// and a game that is force-quit or crashes is exactly the case where you do not want
    /// to have lost a consent decision.
    /// </para>
    /// </summary>
    public sealed class PlayerPrefsConsentStorage : IConsentStorage
    {
        public string? Read(string key) => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;

        public void Write(string key, string value)
        {
            PlayerPrefs.SetString(key, value);
            PlayerPrefs.Save();
        }

        public void Delete(string key)
        {
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }
    }

    /// <summary>
    /// HTTP over <c>UnityWebRequest</c>, which works on every platform Unity builds for —
    /// including WebGL, where <c>HttpClient</c> does not.
    /// </summary>
    public sealed class UnityWebRequestTransport : IConsentTransport
    {
        private readonly int _timeoutSeconds;

        public UnityWebRequestTransport(int timeoutSeconds = 10) => _timeoutSeconds = timeoutSeconds;

        public async Task PostAsync(string url, string region, string jsonBody)
        {
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(jsonBody)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = _timeoutSeconds,
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-CookieMunch-Region", region);
            // The result is discarded on purpose: the decision was persisted locally before
            // this was called, and a failed sync is retried on the next commit rather than
            // surfaced to the game.
            await Send(request).ConfigureAwait(false);
        }

        public Task<string?> GetAsync(string url, string region)
        {
            var request = UnityWebRequest.Get(url);
            request.timeout = _timeoutSeconds;
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("X-CookieMunch-Region", region);
            return Send(request);
        }

        /// <summary>
        /// Bridges Unity's coroutine-shaped async operation to a Task. Completes with null on
        /// any failure rather than faulting: every caller in this SDK treats an error as "no
        /// answer", and a faulted Task here would only be caught and discarded one frame later.
        /// </summary>
        private static Task<string?> Send(UnityWebRequest request)
        {
            var tcs = new TaskCompletionSource<string?>();
            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                try
                {
                    var ok = request.result == UnityWebRequest.Result.Success;
                    tcs.TrySetResult(ok ? request.downloadHandler?.text : null);
                }
                catch
                {
                    tcs.TrySetResult(null);
                }
                finally
                {
                    request.Dispose();
                }
            };
            return tcs.Task;
        }
    }

    public static class CookieMunchUnity
    {
        /// <summary>
        /// A client wired for a game: PlayerPrefs storage, UnityWebRequest transport, and the
        /// device's country as the starting region.
        /// <para>
        /// That region is a STARTING POINT, not the answer — the system locale says where the
        /// device was sold. Call <c>RefreshRegulationAsync()</c> once at startup to replace it
        /// with the regime the server resolves from the IP it actually sees.
        /// </para>
        /// </summary>
        public static CookieMunchConsent Create(string cbid, string apiUrl, string? region = null) =>
            new(
                cbid,
                apiUrl,
                new PlayerPrefsConsentStorage(),
                new UnityWebRequestTransport(),
                region ?? DeviceRegion());

        /// <summary>
        /// The device's own ISO 3166-1 alpha-2 country, or "unknown".
        /// <para>
        /// Read from <c>RegionInfo.CurrentRegion</c> rather than derived from
        /// <c>Application.systemLanguage</c>: a language is not a country, and the cases
        /// where they diverge are the ones that change the answer — most Portuguese
        /// speakers are in Brazil (LGPD), not Portugal (GDPR), and English says nothing at
        /// all about whether CCPA or UK-GDPR applies.
        /// </para>
        /// <para>
        /// Even so, this is where the device was SET UP, not where its owner is now.
        /// "unknown" resolves to the strict opt-in default, and
        /// <c>RefreshRegulationAsync()</c> replaces the whole thing with the regime the
        /// server resolves from the IP it actually sees.
        /// </para>
        /// </summary>
        private static string DeviceRegion()
        {
            try
            {
                var code = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
                return string.IsNullOrEmpty(code) ? "unknown" : code.ToLowerInvariant();
            }
            catch
            {
                // Some IL2CPP/WebGL configurations trim the ICU data this depends on.
                return "unknown";
            }
        }
    }
}
#endif
