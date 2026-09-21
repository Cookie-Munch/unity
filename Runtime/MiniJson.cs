using System;
using System.Text;

namespace CookieMunch
{
    /// <summary>
    /// A deliberately tiny JSON reader: enough to pull named values out of the one object
    /// shape this SDK consumes, and nothing else.
    /// <para>
    /// It exists because Unity has no dependency-free JSON parser that also runs under
    /// <c>dotnet test</c>. <c>JsonUtility</c> is Unity-only, so it could not be tested here;
    /// Newtonsoft is a 600KB dependency to read eight fields. This is a real scanner rather
    /// than regular expressions, because the things that break naive extraction — a key name
    /// appearing inside a string value, an escaped quote, a nested object — are exactly what
    /// arrives from a live server.
    /// </para>
    /// <para>
    /// It only reads top-level members of the object it is handed. It does not validate the
    /// document, and every accessor returns null rather than throwing: a malformed response
    /// must leave the caller on its locally-resolved answer, not crash the game.
    /// </para>
    /// </summary>
    internal static class MiniJson
    {
        /// <summary>The raw text of <paramref name="key"/>'s object value, or null.</summary>
        public static string? GetObject(string json, string key) => Slice(json, key, '{', '}');

        /// <summary>The unescaped string value of <paramref name="key"/>, or null.</summary>
        public static string? GetString(string json, string key)
        {
            var at = FindValue(json, key);
            if (at < 0 || at >= json.Length || json[at] != '"') return null;
            return ReadString(json, at, out _);
        }

        /// <summary>The boolean value of <paramref name="key"/>, or null if absent/not a bool.</summary>
        public static bool? GetBool(string json, string key)
        {
            var at = FindValue(json, key);
            if (at < 0) return null;
            if (string.CompareOrdinal(json, at, "true", 0, 4) == 0) return true;
            if (string.CompareOrdinal(json, at, "false", 0, 5) == 0) return false;
            return null;
        }

        /// <summary>The integer value of <paramref name="key"/>, or null.</summary>
        public static int? GetInt(string json, string key) =>
            GetLong(json, key) is { } l && l >= int.MinValue && l <= int.MaxValue ? (int)l : null;

        /// <summary>The integer value of <paramref name="key"/>, or null if absent/not a number.</summary>
        public static long? GetLong(string json, string key)
        {
            var at = FindValue(json, key);
            if (at < 0) return null;
            var end = at;
            if (end < json.Length && (json[end] == '-' || json[end] == '+')) end++;
            while (end < json.Length && json[end] >= '0' && json[end] <= '9') end++;
            return long.TryParse(
                json.Substring(at, end - at),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
                ? value
                : null;
        }

        /// <summary>
        /// Index of the first character of <paramref name="key"/>'s value at the top level of
        /// this object, or -1. Walks the document rather than searching for the key text, so a
        /// key name that also appears inside a nested object or a string value is not matched.
        /// </summary>
        private static int FindValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return -1;
            var i = SkipWs(json, 0);
            if (i >= json.Length || json[i] != '{') return -1;
            i++;

            while (true)
            {
                i = SkipWs(json, i);
                if (i >= json.Length || json[i] == '}') return -1;
                if (json[i] != '"') return -1; // not a well-formed member list

                var name = ReadString(json, i, out i);
                if (name == null) return -1;

                i = SkipWs(json, i);
                if (i >= json.Length || json[i] != ':') return -1;
                i = SkipWs(json, i + 1);
                if (i >= json.Length) return -1;

                if (name == key) return i;

                i = SkipValue(json, i);
                if (i < 0) return -1;
                i = SkipWs(json, i);
                if (i < json.Length && json[i] == ',') i++;
                else return -1; // end of object, or malformed
            }
        }

        /// <summary>The raw text of a bracketed value, braces included.</summary>
        private static string? Slice(string json, string key, char open, char close)
        {
            var at = FindValue(json, key);
            if (at < 0 || at >= json.Length || json[at] != open) return null;
            var end = SkipValue(json, at);
            return end < 0 ? null : json.Substring(at, end - at);
        }

        private static int SkipWs(string s, int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
            return i;
        }

        /// <summary>Index just past the value starting at <paramref name="i"/>, or -1.</summary>
        private static int SkipValue(string s, int i)
        {
            if (i >= s.Length) return -1;
            switch (s[i])
            {
                case '"':
                    return ReadString(s, i, out var afterString) == null ? -1 : afterString;
                case '{':
                    return SkipBracketed(s, i, '{', '}');
                case '[':
                    return SkipBracketed(s, i, '[', ']');
                default:
                    // A literal: number, true, false, null. Ends at the next structural char.
                    while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']') i++;
                    return i;
            }
        }

        /// <summary>
        /// Index just past a bracketed value. Counts depth while honouring strings, so a brace
        /// inside a string value does not unbalance it.
        /// </summary>
        private static int SkipBracketed(string s, int i, char open, char close)
        {
            var depth = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '"')
                {
                    if (ReadString(s, i, out i) == null) return -1;
                    continue;
                }

                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i + 1;
                }

                i++;
            }

            return -1;
        }

        /// <summary>
        /// Read the string literal starting at <paramref name="i"/> (which must be its opening
        /// quote), unescaping it. <paramref name="after"/> lands just past the closing quote.
        /// Returns null on an unterminated literal.
        /// </summary>
        private static string? ReadString(string s, int i, out int after)
        {
            after = i;
            if (i >= s.Length || s[i] != '"') return null;
            i++;

            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '"')
                {
                    after = i + 1;
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                i++;
                if (i >= s.Length) return null;
                switch (s[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 >= s.Length) return null;
                        if (!ushort.TryParse(
                                s.Substring(i + 1, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var code)) return null;
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: sb.Append(s[i]); break; // covers \" \\ \/
                }

                i++;
            }

            return null; // unterminated
        }
    }
}
