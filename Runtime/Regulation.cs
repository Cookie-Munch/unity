using System;
using System.Collections.Generic;

namespace CookieMunch
{
    /// <summary>Opt-in ("ask before anything fires") vs opt-out ("fire, but honour a refusal").</summary>
    public enum ConsentModel
    {
        OptIn,
        OptOut,
    }

    /// <summary>Coarse jurisdiction bucket.</summary>
    public enum RegionClass
    {
        Eu,
        Us,
        Br,
        Ca,
        Other,
    }

    /// <summary>The signalling framework third parties will read.</summary>
    public enum SignalFramework
    {
        Tcf,
        Gpp,
        None,
    }

    /// <summary>
    /// Which privacy regime applies to a player, and what that means for the game.
    /// <para>
    /// A C# port of <c>packages/geo/src/regulation.ts</c> — the same table as the web embed
    /// and the iOS, Android and Flutter SDKs, so the five cannot disagree about someone's
    /// rights. Resolving locally costs nothing and works offline, but see
    /// <see cref="CookieMunchConsent.RefreshRegulationAsync"/>: a device's locale says where
    /// the phone was sold, not where its owner is standing.
    /// </para>
    /// </summary>
    public sealed class Regulation
    {
        /// <summary>ISO 3166-1 alpha-2, optionally with a subdivision ("us-ca").</summary>
        public string Region { get; }

        public RegionClass Class { get; }

        public bool GdprApplies { get; }

        public bool CcpaApplies { get; }

        public bool LgpdApplies { get; }

        public ConsentModel Model { get; }

        /// <summary>What categories default to before the player has said anything.</summary>
        public bool DefaultGranted { get; }

        public SignalFramework Framework { get; }

        /// <summary>A platform-level signal (GPC, DNT) already expressed a refusal for them.</summary>
        public bool ForcedOptOut { get; }

        /// <summary>
        /// Whether a decision still has to be collected. See
        /// <see cref="CookieMunchConsent.IsConsentRequired"/>, which also accounts for a
        /// decision this player already made in the game.
        /// </summary>
        public bool ConsentRequired { get; }

        public Regulation(
            string region,
            RegionClass regionClass,
            bool gdprApplies,
            bool ccpaApplies,
            bool lgpdApplies,
            ConsentModel model,
            bool defaultGranted,
            SignalFramework framework,
            bool forcedOptOut,
            bool consentRequired)
        {
            Region = region ?? string.Empty;
            Class = regionClass;
            GdprApplies = gdprApplies;
            CcpaApplies = ccpaApplies;
            LgpdApplies = lgpdApplies;
            Model = model;
            DefaultGranted = defaultGranted;
            Framework = framework;
            ForcedOptOut = forcedOptOut;
            ConsentRequired = consentRequired;
        }

        // EU 27 + EEA + UK, lowercase ISO 3166-1 alpha-2.
        private static readonly HashSet<string> EuEeaUk = new HashSet<string>(StringComparer.Ordinal)
        {
            "at", "be", "bg", "hr", "cy", "cz", "dk", "ee", "fi", "fr", "de", "gr", "hu", "ie",
            "it", "lv", "lt", "lu", "mt", "nl", "pl", "pt", "ro", "sk", "si", "es", "se",
            "is", "li", "no",
            "gb", "uk",
        };

        public static RegionClass Classify(string region)
        {
            if (string.IsNullOrEmpty(region)) return RegionClass.Other;
            var lower = region.ToLowerInvariant();
            var dash = lower.IndexOf('-');
            var country = dash >= 0 ? lower.Substring(0, dash) : lower;
            if (EuEeaUk.Contains(country)) return RegionClass.Eu;
            if (country == "us") return RegionClass.Us;
            if (country == "br") return RegionClass.Br;
            if (country == "ca") return RegionClass.Ca;
            return RegionClass.Other;
        }

        /// <summary>
        /// Resolve the regime for a region and the opt-out signals available on device.
        /// </summary>
        /// <param name="region">ISO 3166-1 alpha-2, optionally with a subdivision ("us-ca").</param>
        /// <param name="gpc">Global Privacy Control, if your game surfaces one.</param>
        /// <param name="dnt">The legacy Do Not Track signal.</param>
        /// <param name="honorDnt">Treat <paramref name="dnt"/> as a refusal. Default true.</param>
        /// <param name="unknownModel">
        /// The regime for regions we don't recognise. Default opt-in, which is the safe
        /// direction to be wrong in.
        /// </param>
        public static Regulation Resolve(
            string region,
            bool gpc = false,
            bool dnt = false,
            bool honorDnt = true,
            ConsentModel unknownModel = ConsentModel.OptIn)
        {
            var cls = Classify(region);

            ConsentModel model;
            SignalFramework framework;
            switch (cls)
            {
                case RegionClass.Us:
                    model = ConsentModel.OptOut;
                    framework = SignalFramework.Gpp;
                    break;
                case RegionClass.Eu:
                    model = ConsentModel.OptIn;
                    framework = SignalFramework.Tcf;
                    break;
                case RegionClass.Br:
                case RegionClass.Ca:
                    model = ConsentModel.OptIn;
                    framework = SignalFramework.None;
                    break;
                default:
                    model = unknownModel;
                    framework = SignalFramework.None;
                    break;
            }

            // GPC/DNT only matter where collection would otherwise proceed. Under an opt-in
            // regime nothing fires before consent anyway, so there is nothing to force.
            var forced = model == ConsentModel.OptOut && (gpc || (honorDnt && dnt));

            return new Regulation(
                region ?? string.Empty,
                cls,
                cls == RegionClass.Eu,
                cls == RegionClass.Us,
                cls == RegionClass.Br,
                model,
                model == ConsentModel.OptOut,
                framework,
                forced,
                !forced);
        }

        /// <summary>
        /// Parse the <c>regulation</c> block of a <c>/config/:cbid</c> response. Returns null
        /// when the block is absent (an older server) or malformed — the caller then keeps
        /// whatever it resolved locally.
        /// </summary>
        public static Regulation? FromConfigJson(string body)
        {
            var reg = MiniJson.GetObject(body, "regulation");
            if (reg == null) return null;

            var flags = MiniJson.GetObject(reg, "regulations") ?? "{}";
            var forced = MiniJson.GetBool(reg, "forcedOptOut") ?? false;

            return new Regulation(
                MiniJson.GetString(reg, "region") ?? string.Empty,
                ParseClass(MiniJson.GetString(reg, "class")),
                MiniJson.GetBool(flags, "gdprApplies") ?? false,
                MiniJson.GetBool(flags, "ccpaApplies") ?? false,
                MiniJson.GetBool(flags, "lgpdApplies") ?? false,
                MiniJson.GetString(reg, "model") == "opt-out" ? ConsentModel.OptOut : ConsentModel.OptIn,
                MiniJson.GetString(reg, "defaultState") == "granted",
                ParseFramework(MiniJson.GetString(reg, "framework")),
                forced,
                // An older server could omit this. Defaulting a missing bool to false would
                // suppress every prompt on the planet, so it is derived instead.
                MiniJson.GetBool(reg, "consentRequired") ?? !forced);
        }

        // Lenient on purpose: a server that learns a new jurisdiction tomorrow must not
        // crash a build shipped today. An unknown value degrades to the safest reading.
        private static RegionClass ParseClass(string? value) => value switch
        {
            "eu" => RegionClass.Eu,
            "us" => RegionClass.Us,
            "br" => RegionClass.Br,
            "ca" => RegionClass.Ca,
            _ => RegionClass.Other,
        };

        private static SignalFramework ParseFramework(string? value) => value switch
        {
            "tcf" => SignalFramework.Tcf,
            "gpp" => SignalFramework.Gpp,
            _ => SignalFramework.None,
        };

        public override string ToString() =>
            $"Regulation(region: {Region}, class: {Class}, model: {Model}, gdpr: {GdprApplies}, " +
            $"ccpa: {CcpaApplies}, lgpd: {LgpdApplies}, framework: {Framework}, " +
            $"forcedOptOut: {ForcedOptOut}, consentRequired: {ConsentRequired})";
    }
}
