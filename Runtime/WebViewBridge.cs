using System;
using System.Text;

namespace CookieMunch
{
    /// <summary>
    /// Carrying consent from a Unity game into a web view.
    /// <para>
    /// A game collects consent natively, then opens web content — a privacy policy, a
    /// support page, a store. That page runs the web embed, finds no stored decision, and
    /// asks again. The player has now answered the same question twice, and the web side
    /// keeps the second answer.
    /// </para>
    /// <para>
    /// Unity ships no web view of its own, so this emits the two things every plugin can
    /// take: a JavaScript statement to evaluate, and a URL parameter. A port of
    /// <c>packages/core/src/webview-bridge.ts</c>, matching the Swift, Kotlin and Dart
    /// bridges. The contract is that the WEB READER recovers the right decision — not that
    /// the five produce byte-identical strings.
    /// </para>
    /// </summary>
    public static class WebViewBridge
    {
        /// <summary>Query parameter carrying a serialised decision.</summary>
        public const string Param = "cm_consent";

        /// <summary>Cookie the web embed reads.</summary>
        public const string CookieName = "CookieMunch";

        /// <summary>Cookiebot's name, for a game migrating from it.</summary>
        public const string LegacyCookieName = "CookieConsent";

        /// <summary>Twelve months, matching the embed's own default cookie lifetime.</summary>
        public const int DefaultMaxAge = 60 * 60 * 24 * 365;

        /// <summary>The serialised, URL-encoded value the web side stores.</summary>
        public static string Serialize(ConsentState state) => EncodeComponent(state.ToJson());

        /// <summary>
        /// A single JavaScript statement that seeds a web view with this decision.
        /// <para>
        /// One line and expression-only on purpose: every plugin's evaluator takes a string,
        /// and a multi-line program is a common source of silent failures across them.
        /// </para>
        /// </summary>
        public static string JavaScript(ConsentState state, int maxAge = DefaultMaxAge, bool alsoLegacyCookie = false)
        {
            var value = Serialize(state);
            var attrs = $";path=/;max-age={maxAge.ToString(System.Globalization.CultureInfo.InvariantCulture)};SameSite=Lax";
            var write = new Func<string, string>(name =>
                $"document.cookie='{JsString(name)}='+'{JsString(value)}'+'{JsString(attrs)}';");
            return alsoLegacyCookie ? write(CookieName) + write(LegacyCookieName) : write(CookieName);
        }

        /// <summary>
        /// A URL parameter carrying this decision, for when script evaluation is unavailable.
        /// <para>
        /// The value is percent-encoded TWICE, and that is deliberate: <see cref="Serialize"/>
        /// is the cookie value, which is itself already encoded, and the reader unwraps both
        /// layers — once pulling the parameter out of the query string, once turning the
        /// cookie value back into JSON. Encoding only once round-trips for simple values and
        /// then silently corrupts the first decision whose JSON contains a literal '%'.
        /// </para>
        /// </summary>
        public static string QueryString(ConsentState state) => $"{Param}={EncodeComponent(Serialize(state))}";

        /// <summary>
        /// <paramref name="url"/> with the decision attached, replacing any existing parameter
        /// rather than appending a second copy — a web view that reloads its own URL would
        /// otherwise accumulate them until the request line was too long to send.
        /// </summary>
        public static string Url(string url, ConsentState state)
        {
            if (string.IsNullOrEmpty(url)) return url;

            var hashAt = url.IndexOf('#');
            var fragment = hashAt >= 0 ? url.Substring(hashAt) : string.Empty;
            var withoutFragment = hashAt >= 0 ? url.Substring(0, hashAt) : url;

            var queryAt = withoutFragment.IndexOf('?');
            var basePart = queryAt >= 0 ? withoutFragment.Substring(0, queryAt) : withoutFragment;
            var query = queryAt >= 0 ? withoutFragment.Substring(queryAt + 1) : string.Empty;

            var kept = new StringBuilder();
            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                var name = eq >= 0 ? pair.Substring(0, eq) : pair;
                if (name == Param) continue; // replace, do not duplicate
                if (kept.Length > 0) kept.Append('&');
                kept.Append(pair);
            }

            if (kept.Length > 0) kept.Append('&');
            kept.Append(QueryString(state));
            return $"{basePart}?{kept}{fragment}";
        }

        /// <summary>Escape for embedding inside a single-quoted JavaScript string literal.</summary>
        internal static string JsString(string value)
        {
            var sb = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\'': sb.Append("\\'"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    // `</script` would close an inline tag if this were ever inlined.
                    case '<': sb.Append("\\x3c"); break;
                    default: sb.Append(c); break;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// JavaScript's <c>encodeURIComponent</c>, exactly. Neither <c>Uri.EscapeDataString</c>
        /// nor <c>UnityWebRequest.EscapeURL</c> matches it — the first escapes <c>!'()*</c>,
        /// the second form-encodes spaces as '+' — and either difference changes what the
        /// reader on the web side gets back.
        /// </summary>
        internal static string EncodeComponent(string value)
        {
            const string unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.!~*'()";
            var sb = new StringBuilder(value.Length * 2);
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                var c = (char)b;
                if (unreserved.IndexOf(c) >= 0) sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
