using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CookieMunch
{
    /// <summary>
    /// The Cookie Munch consent client for Unity — the framework-agnostic core.
    /// <para>
    /// No <c>UnityEngine</c> references, so it runs on every target Unity builds for and is
    /// testable with plain <c>dotnet test</c>. The design mirrors the iOS, Android, Flutter
    /// and React Native clients: an implied default, pluggable storage, load/accept/decline/
    /// submit, a change event, region awareness and best-effort sync. API failures are
    /// swallowed — a consent decision is NEVER lost because the device was offline.
    /// </para>
    /// <para>
    /// A game usually wants <c>CookieMunchConsent.Create()</c> from the Unity assembly, which
    /// wires PlayerPrefs storage and a UnityWebRequest transport.
    /// </para>
    /// </summary>
    public sealed class CookieMunchConsent
    {
        private readonly string _cbid;
        private readonly string _apiUrl;
        private readonly IConsentStorage _storage;
        private readonly IConsentTransport _transport;
        private readonly string _storageKey;
        private readonly Func<long> _now;
        private readonly Func<string> _stamp;
        private readonly List<Action<ConsentState>> _listeners = new();
        private readonly Dictionary<ConsentCategory, List<Action>> _gates = new();

        private ConsentState _state;
        private Regulation? _serverRegulation;
        private bool _gpc;
        private bool _dnt;

        /// <summary>
        /// Who this device's decisions belong to, if the game has said. Deliberately NOT
        /// persisted with the decision: who is signed in is the game's business and can
        /// change between launches, so baking a stale account id into a restored record
        /// would attribute one player's consent to another.
        /// </summary>
        private string? _subjectId;

        /// <param name="cbid">Your Cookie Munch site id.</param>
        /// <param name="apiUrl">Base URL of your server; a trailing slash is fine.</param>
        /// <param name="region">The player's region, sent as <c>X-CookieMunch-Region</c>.</param>
        public CookieMunchConsent(
            string cbid,
            string apiUrl,
            IConsentStorage? storage = null,
            IConsentTransport? transport = null,
            string region = "unknown",
            string storageKey = "CookieMunch",
            string? subjectId = null,
            Func<long>? now = null,
            Func<string>? stamp = null)
        {
            _cbid = cbid;
            _apiUrl = (apiUrl ?? string.Empty).TrimEnd('/');
            _storage = storage ?? new InMemoryConsentStorage();
            _transport = transport ?? new NullConsentTransport();
            Region = region ?? "unknown";
            _storageKey = storageKey;
            _subjectId = string.IsNullOrEmpty(subjectId) ? null : subjectId;
            _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _stamp = stamp ?? (() => Guid.NewGuid().ToString());
            _state = new ConsentState(false, false, false, ConsentMethod.Implied, _stamp(), 1, _now(), Region);
        }

        /// <summary>Coarse region tag this client was configured with.</summary>
        public string Region { get; }

        /// <summary>The current decision.</summary>
        public ConsentState State => _state;

        /// <summary>True once the player has made an explicit choice.</summary>
        public bool HasResponse => _state.HasResponse;

        /// <summary>Whether <paramref name="category"/> is granted. Necessary always is.</summary>
        public bool Allows(ConsentCategory category) => _state.Allows(category);

        /// <summary>Fired on every committed decision. Listener exceptions are isolated.</summary>
        public event Action<ConsentState>? Changed;

        /// <summary>Register a change callback; the returned action unsubscribes.</summary>
        public Action OnChange(Action<ConsentState> listener)
        {
            _listeners.Add(listener);
            return () => _listeners.Remove(listener);
        }

        // --- applicable regulation ------------------------------------------------

        /// <summary>
        /// Which privacy regime applies to this player: GDPR / CCPA / LGPD, opt-in vs
        /// opt-out, and which signalling framework applies.
        /// <para>
        /// Answers immediately and offline from the region this client was configured with.
        /// Call <see cref="RefreshRegulationAsync"/> to replace that with the server's
        /// IP-derived answer — a device's locale tells you where it was sold, not where its
        /// owner is standing.
        /// </para>
        /// </summary>
        public Regulation ApplicableRegulation =>
            _serverRegulation ?? Regulation.Resolve(Region, _gpc, _dnt);

        /// <summary>
        /// Whether you still owe this player a consent prompt.
        /// <para>
        /// False once they have made an explicit decision, and false when an opt-out signal
        /// has already expressed a refusal on their behalf. Check this before showing your
        /// consent screen: a game that re-prompts on every cold start is both annoying and,
        /// under an opt-out regime, wrong.
        /// </para>
        /// </summary>
        public bool IsConsentRequired => !_state.HasResponse && ApplicableRegulation.ConsentRequired;

        /// <summary>
        /// Record a Global Privacy Control signal. Under an opt-out regime this counts as a
        /// refusal on the player's behalf, so no prompt is owed; under GDPR nothing fires
        /// before consent anyway, so the prompt still is.
        /// </summary>
        public void SetGlobalPrivacyControl(bool enabled)
        {
            _gpc = enabled;
            _serverRegulation = null; // the local resolver now has newer information
        }

        /// <summary>Record a legacy Do Not Track signal. Treated exactly like GPC.</summary>
        public void SetDoNotTrack(bool enabled)
        {
            _dnt = enabled;
            _serverRegulation = null;
        }

        // --- cross-surface identity -----------------------------------------------

        /// <summary>The account id currently attached to this device's decisions, or null.</summary>
        public string? SubjectId => _subjectId;

        /// <summary>
        /// Attach this device's decisions to a signed-in account, so one player's consent
        /// can be correlated across web, mobile and desktop
        /// (<c>GET /v1/subjects/:id/consent</c>).
        /// <para>
        /// Call it after sign-in rather than at construction: a game builds its consent
        /// client at launch, before anyone has signed in. Pass null on sign-out —
        /// continuing to send the id would attribute the next player's decisions on a
        /// shared device to the account that just left.
        /// </para>
        /// <para>
        /// The id is opaque to us: stored and bound into the tamper-evident hash chain,
        /// never interpreted. It applies to decisions made from now on; it does not
        /// rewrite history.
        /// </para>
        /// </summary>
        public void SetSubjectId(string? id) => _subjectId = string.IsNullOrEmpty(id) ? null : id;

        /// <summary>
        /// Ask the server which regime applies, based on the IP it sees, and adopt the answer.
        /// Never throws: offline, or against a server too old to return a regulation block,
        /// the locally resolved regime stays in place — a failed refresh must never leave the
        /// game with no answer to "do I prompt".
        /// </summary>
        public async Task<Regulation> RefreshRegulationAsync()
        {
            try
            {
                var body = await _transport.GetAsync($"{_apiUrl}/config/{Uri.EscapeDataString(_cbid)}", Region)
                    .ConfigureAwait(false);
                if (body != null)
                {
                    var parsed = Regulation.FromConfigJson(body);
                    if (parsed != null) _serverRegulation = parsed;
                }
            }
            catch
            {
                // offline, or a malformed response — keep the local answer.
            }

            return ApplicableRegulation;
        }

        // --- lifecycle ------------------------------------------------------------

        /// <summary>
        /// Restore any persisted decision. A corrupt or absent record leaves the implied
        /// default in place, so a bad write can never lock a player out of being asked.
        /// </summary>
        public ConsentState Load()
        {
            var raw = _storage.Read(_storageKey);
            if (raw != null)
            {
                var parsed = ConsentState.FromJson(raw, Region, _now());
                if (parsed != null) Emit(parsed);
            }

            return _state;
        }

        /// <summary>Grant every category (explicit).</summary>
        public Task<ConsentState> AcceptAsync() => CommitAsync(true, true, true);

        /// <summary>Refuse every non-necessary category (explicit).</summary>
        public Task<ConsentState> DeclineAsync() => CommitAsync(false, false, false);

        /// <summary>Submit a specific set (explicit).</summary>
        public Task<ConsentState> SubmitAsync(bool preferences, bool statistics, bool marketing) =>
            CommitAsync(preferences, statistics, marketing);

        /// <summary>Set one category, keeping the others as they are.</summary>
        public Task<ConsentState> SetAsync(ConsentCategory category, bool granted) => CommitAsync(
            category == ConsentCategory.Preferences ? granted : _state.Preferences,
            category == ConsentCategory.Statistics ? granted : _state.Statistics,
            category == ConsentCategory.Marketing ? granted : _state.Marketing);

        /// <summary>
        /// Run <paramref name="action"/> once <paramref name="category"/> is granted — the
        /// native equivalent of the web embed's prior-blocking, and the right way to start an
        /// ad or analytics SDK. Returns an action that cancels a still-pending gate.
        /// <para>
        /// Initialising an ads SDK and hoping to stop it later is not prior-blocking: by then
        /// it has already opened a connection and read an advertising id.
        /// </para>
        /// </summary>
        public Action Gate(ConsentCategory category, Action action)
        {
            if (_state.Allows(category))
            {
                Isolate(action);
                return () => { };
            }

            if (!_gates.TryGetValue(category, out var pending))
            {
                pending = new List<Action>();
                _gates[category] = pending;
            }

            pending.Add(action);
            return () => pending.Remove(action);
        }

        private async Task<ConsentState> CommitAsync(bool preferences, bool statistics, bool marketing)
        {
            Emit(_state.With(preferences, statistics, marketing, ConsentMethod.Explicit, _now()));
            _storage.Write(_storageKey, _state.ToJson());
            await SyncAsync().ConfigureAwait(false);
            return _state;
        }

        private void Emit(ConsentState next)
        {
            _state = next;

            foreach (var category in new[]
                     {
                         ConsentCategory.Preferences, ConsentCategory.Statistics, ConsentCategory.Marketing,
                     })
            {
                if (!next.Allows(category) || !_gates.TryGetValue(category, out var pending)) continue;
                _gates.Remove(category);
                foreach (var action in pending) Isolate(action);
            }

            foreach (var listener in _listeners.ToArray()) Isolate(() => listener(next));
            if (Changed != null) Isolate(() => Changed(next));
        }

        /// <summary>A listener that throws must never break the consent flow.</summary>
        private static void Isolate(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                // swallowed on purpose
            }
        }

        private async Task SyncAsync()
        {
            var body =
                "{\"cbid\":\"" + _cbid + "\"" +
                ",\"stamp\":\"" + _state.Stamp + "\"" +
                ",\"choices\":{\"preferences\":" + (_state.Preferences ? "true" : "false") +
                ",\"statistics\":" + (_state.Statistics ? "true" : "false") +
                ",\"marketing\":" + (_state.Marketing ? "true" : "false") + "}" +
                ",\"method\":\"" + (_state.HasResponse ? "explicit" : "implied") + "\"" +
                ",\"ver\":" + _state.Ver +
                ",\"utc\":" + _state.Utc +
                ",\"url\":\"app://" + _cbid + "\"" +
                // Omitted entirely when absent, so a decision made while signed out is
                // identical to one from a build that never had this field.
                (_subjectId == null ? string.Empty : ",\"subjectId\":\"" + _subjectId + "\"") +
                "}";

            try
            {
                await _transport.PostAsync($"{_apiUrl}/api/v1/consent", Region, body).ConfigureAwait(false);
            }
            catch
            {
                // Offline. The local write above already captured the decision.
            }
        }
    }
}
