namespace Govor.Application.Authentication;

// Ephemeral proofs and authorizations are process-local, bounded and consumed
// exactly once. A restart invalidates pending handshakes, never long-term keys.
public sealed class PasswordChallenges(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    public sealed record Entry(Guid UserId, Guid SessionId, string Purpose, long CredentialVersion,
        PasswordCredentials Credentials, string SecretEphemeral, string PublicEphemeral,
        string DeviceInfo, DateTimeOffset ExpiresAt);

    public Guid Add(Entry entry)
    {
        lock (_gate)
        {
            foreach (var id in _entries.Where(p => p.Value.ExpiresAt <= _clock.GetUtcNow()).Select(p => p.Key).ToArray())
                _entries.Remove(id);
            if (_entries.Count >= 1024) throw new InvalidOperationException("Too many pending password proofs. Retry shortly.");
            var key = Guid.NewGuid(); _entries.Add(key, entry); return key;
        }
    }
    public Entry Consume(Guid id, string purpose, Guid? user = null, Guid? session = null)
    {
        lock (_gate)
        {
            if (!_entries.Remove(id, out var value) || value.ExpiresAt <= _clock.GetUtcNow() ||
                value.Purpose != purpose || (user.HasValue && value.UserId != user) ||
                (session.HasValue && value.SessionId != session)) throw new UnauthorizedAccessException("Password proof is expired or invalid.");
            return value;
        }
    }
    public DateTimeOffset Now => _clock.GetUtcNow();
}
