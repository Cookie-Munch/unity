using System;
using System.Globalization;
using System.Text;

namespace CookieMunch
{
    /// <summary>The four consent categories. <see cref="Necessary"/> is always granted.</summary>
    public enum ConsentCategory
    {
        Necessary,
        Preferences,
        Statistics,
        Marketing,
    }

    /// <summary>How the decision was made.</summary>
    public enum ConsentMethod
    {
        /// <summary>The default in force before the player has chosen.</summary>
        Implied,

        /// <summary>The player chose — accept, decline or a custom set.</summary>
        Explicit,
    }

    /// <summary>
    /// One consent decision. Mirrors the web, iOS, Android, Flutter and React Native SDKs
    /// field for field, so a decision made in a game and one made in a browser are the same
    /// row in the consent ledger.
    /// </summary>
    public sealed class ConsentState
    {
        public bool Necessary => true;

        public bool Preferences { get; }

        public bool Statistics { get; }

        public bool Marketing { get; }

        public ConsentMethod Method { get; }

        /// <summary>Stable id for this decision, carried into the ledger.</summary>
        public string Stamp { get; }

        /// <summary>The consent policy version this decision was made under.</summary>
        public int Ver { get; }

        /// <summary>When the decision was made, in milliseconds since the Unix epoch.</summary>
        public long Utc { get; }

        public string Region { get; }

        public ConsentState(
            bool preferences,
            bool statistics,
            bool marketing,
            ConsentMethod method,
            string stamp,
            int ver,
            long utc,
            string region)
        {
            Preferences = preferences;
            Statistics = statistics;
            Marketing = marketing;
            Method = method;
            Stamp = stamp ?? string.Empty;
            Ver = ver;
            Utc = utc;
            Region = region ?? "unknown";
        }

        /// <summary>True once the player has made an explicit choice.</summary>
        public bool HasResponse => Method == ConsentMethod.Explicit;

        /// <summary>True if any non-necessary category is granted.</summary>
        public bool Consented => Preferences || Statistics || Marketing;

        /// <summary>Whether <paramref name="category"/> is granted. Necessary always is.</summary>
        public bool Allows(ConsentCategory category) => category switch
        {
            ConsentCategory.Necessary => true,
            ConsentCategory.Preferences => Preferences,
            ConsentCategory.Statistics => Statistics,
            ConsentCategory.Marketing => Marketing,
            _ => false,
        };

        public ConsentState With(bool preferences, bool statistics, bool marketing, ConsentMethod method, long utc) =>
            new ConsentState(preferences, statistics, marketing, method, Stamp, Ver, utc, Region);

        /// <summary>
        /// The JSON the other five SDKs write. Hand-built rather than reflected so the key
        /// order and the exact shape are visible here and cannot drift with a serializer
        /// setting — this string ends up in a cookie a browser reads.
        /// </summary>
        public string ToJson()
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"necessary\":true");
            sb.Append(",\"preferences\":").Append(Preferences ? "true" : "false");
            sb.Append(",\"statistics\":").Append(Statistics ? "true" : "false");
            sb.Append(",\"marketing\":").Append(Marketing ? "true" : "false");
            sb.Append(",\"method\":\"").Append(Method == ConsentMethod.Explicit ? "explicit" : "implied").Append('"');
            sb.Append(",\"stamp\":\"").Append(Escape(Stamp)).Append('"');
            sb.Append(",\"ver\":").Append(Ver.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"utc\":").Append(Utc.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"region\":\"").Append(Escape(Region)).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Restore a persisted decision, or null if the text is not one.</summary>
        public static ConsentState? FromJson(string json, string fallbackRegion = "unknown", long fallbackUtc = 0)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var stamp = MiniJson.GetString(json, "stamp");
            if (stamp == null) return null;
            return new ConsentState(
                MiniJson.GetBool(json, "preferences") ?? false,
                MiniJson.GetBool(json, "statistics") ?? false,
                MiniJson.GetBool(json, "marketing") ?? false,
                MiniJson.GetString(json, "method") == "explicit" ? ConsentMethod.Explicit : ConsentMethod.Implied,
                stamp,
                MiniJson.GetInt(json, "ver") ?? 1,
                MiniJson.GetLong(json, "utc") ?? fallbackUtc,
                MiniJson.GetString(json, "region") ?? fallbackRegion);
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
