#nullable enable
using System.Collections.Generic;

namespace CookieMunch
{
    /// <summary>
    /// The banner's words in the player's language, resolved by the server.
    ///
    /// Forty languages will not fit in a game build, and five native SDKs each shipping their
    /// own catalogue is five chances to disagree about what one banner says. So the platform
    /// resolves the copy the same way it resolves the regulatory regime — once, server-side —
    /// and this is that answer. It is null until <see cref="CookieMunchConsent.RefreshRegulationAsync"/>
    /// has run; a prompt falls back to its English strings, so a game that has never reached
    /// the network still asks.
    /// </summary>
    public sealed class LocalizedCopy
    {
        /// <summary>What one category is called, and what it means.</summary>
        public sealed class CategoryText
        {
            public CategoryText(string label, string description)
            {
                Label = label;
                Description = description;
            }

            public string Label { get; }
            public string Description { get; }
        }

        internal LocalizedCopy(
            string language,
            bool rtl,
            string? title,
            string? body,
            string? acceptAll,
            string? rejectAll,
            string? save,
            string? customize,
            IReadOnlyDictionary<string, CategoryText> categories,
            string? reopen)
        {
            Language = language;
            Rtl = rtl;
            Title = title;
            Body = body;
            AcceptAll = acceptAll;
            RejectAll = rejectAll;
            Save = save;
            Customize = customize;
            Categories = categories;
            Reopen = reopen;
        }

        /// <summary>The language actually used, which may be the site's default.</summary>
        public string Language { get; }

        /// <summary>Written right to left. A prompt mirrors its layout, not merely its text.</summary>
        public bool Rtl { get; }

        public string? Title { get; }
        public string? Body { get; }
        public string? AcceptAll { get; }
        public string? RejectAll { get; }
        public string? Save { get; }
        public string? Customize { get; }

        /// <summary>Keyed by category id: necessary, preferences, statistics, marketing.</summary>
        public IReadOnlyDictionary<string, CategoryText> Categories { get; }

        /// <summary>The label on the affordance that reopens the prompt.</summary>
        public string? Reopen { get; }

        /// <summary>The four categories. Fixed, so they are read by name.</summary>
        private static readonly string[] CategoryIds = { "necessary", "preferences", "statistics", "marketing" };

        /// <summary>
        /// Read the <c>copy</c> block of a <c>/config/:cbid</c> response. Null when absent or
        /// malformed — a damaged body must never break a prompt, it just falls back to English.
        /// </summary>
        public static LocalizedCopy? FromConfigJson(string body)
        {
            var copy = MiniJson.GetObject(body, "copy");
            if (copy == null) return null;

            var banner = MiniJson.GetObject(copy, "banner") ?? "{}";
            var categoriesJson = MiniJson.GetObject(copy, "categories") ?? "{}";
            var categories = new Dictionary<string, CategoryText>();
            foreach (var id in CategoryIds)
            {
                var entry = MiniJson.GetObject(categoriesJson, id);
                if (entry == null) continue;
                categories[id] = new CategoryText(
                    MiniJson.GetString(entry, "label") ?? string.Empty,
                    MiniJson.GetString(entry, "description") ?? string.Empty);
            }

            return new LocalizedCopy(
                MiniJson.GetString(copy, "language") ?? "en",
                MiniJson.GetBool(copy, "rtl") ?? false,
                Text(banner, "title"),
                Text(banner, "body"),
                Text(banner, "acceptAll"),
                Text(banner, "rejectAll"),
                Text(banner, "save"),
                Text(banner, "customize"),
                categories,
                Text(copy, "reopen"));
        }

        /// <summary>An absent or empty string is nothing, not an empty label.</summary>
        private static string? Text(string json, string key)
        {
            var value = MiniJson.GetString(json, key);
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
