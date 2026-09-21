using System.Threading.Tasks;

namespace CookieMunch
{
    /// <summary>
    /// Where the decision is persisted. Injectable so the client can be unit-tested without
    /// a Unity player loop; in a game, use <c>PlayerPrefsConsentStorage</c>.
    /// </summary>
    public interface IConsentStorage
    {
        string? Read(string key);

        void Write(string key, string value);

        void Delete(string key);
    }

    /// <summary>
    /// The network seam. Implementations may throw; the client swallows everything, because
    /// a consent decision is never lost to a flaky connection.
    /// </summary>
    public interface IConsentTransport
    {
        Task PostAsync(string url, string region, string jsonBody);

        /// <summary>
        /// Fetch a resource. Used only to refresh the applicable regulation from
        /// <c>/config/:cbid</c>. Returning null means "no answer", and the client then stays
        /// with the regime it resolved locally.
        /// </summary>
        Task<string?> GetAsync(string url, string region);
    }

    /// <summary>Keeps the decision in memory only. The default, and what tests use.</summary>
    public sealed class InMemoryConsentStorage : IConsentStorage
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _store = new();

        public string? Read(string key) => _store.TryGetValue(key, out var v) ? v : null;

        public void Write(string key, string value) => _store[key] = value;

        public void Delete(string key) => _store.Remove(key);
    }

    /// <summary>Drops everything. For a game that has not wired the API yet.</summary>
    public sealed class NullConsentTransport : IConsentTransport
    {
        public Task PostAsync(string url, string region, string jsonBody) => Task.CompletedTask;

        public Task<string?> GetAsync(string url, string region) => Task.FromResult<string?>(null);
    }
}
