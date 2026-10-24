using System.Security.Cryptography;
using Govor.Application.Groups;
using Govor.Application.Messages;
using Govor.Domain;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models.Users.Crypto;
using Microsoft.EntityFrameworkCore;

namespace Govor.Application.E2ee;

public sealed class E2eeService(GovorDbContext context)
{
    public async Task<bool> IsRequiredAsync(Guid conversation, RecipientType type) =>
        type == RecipientType.User || await context.ChatGroups.AnyAsync(g => g.Id == conversation && g.IsPrivate);

    public async Task<ConversationKeys> GetConversationKeysAsync(Guid user, Guid conversation, RecipientType type)
    {
        if (!await context.HasChatAccessAsync(user, conversation, type))
            throw new UnauthorizedAccessException("You are not a member of this conversation.");
        Guid[] members;
        if (type == RecipientType.User)
        {
            var chat = await context.PrivateChats.AsNoTracking().SingleAsync(c => c.Id == conversation);
            members = new[] { chat.UserAId, chat.UserBId }.Distinct().Order().ToArray();
        }
        else if (type == RecipientType.Group)
            members = await context.GroupMemberships.AsNoTracking()
                .Where(m => m.GroupId == conversation && !m.IsBanned).Select(m => m.UserId)
                .Distinct().OrderBy(id => id).ToArrayAsync();
        else throw new ArgumentException("Invalid conversation type.");

        // Unexpired sessions are needed for authentication; encryption keys must also
        // remain available for offline devices until their session is explicitly revoked.
        var sessions = await context.UserCryptoSessions.AsNoTracking().Include(c => c.SignedPreKey)
            .Include(c => c.UserSession)
            .Where(c => members.Contains(c.UserSession.UserId) && !c.UserSession.IsRevoked &&
                (c.AccountIdentityKey != null || !context.E2eeRecoveryVaults.Any(v => v.UserId == c.UserSession.UserId)))
            .Take(E2eeProtocol.MaxDevices + 1).ToListAsync();
        var devices = sessions.Select(s => ToDeviceKey(s)).ToArray();
        if (devices.Length > E2eeProtocol.MaxDevices)
            throw new InvalidOperationException("Too many E2EE devices in this conversation.");
        foreach (var key in devices) E2eeProtocol.ValidatePublicKey(key);
        if (members.Any(m => !devices.Any(k => k.UserId == m)))
            throw new InvalidOperationException("Every participant must register E2EE keys before sending messages.");
        return new(conversation, (int)type, members, devices);
    }

    public async Task RegisterAsync(Guid user, Guid sessionId, DeviceKey key)
    {
        if (key.UserId != user || key.SessionId != sessionId)
            throw new UnauthorizedAccessException("E2EE key must belong to the current session.");
        E2eeProtocol.ValidatePublicKey(key);
        var account = await context.E2eeRecoveryVaults.AsNoTracking().SingleOrDefaultAsync(v => v.UserId == user);
        if (account is not null && key.AccountIdentityKey != account.IdentityKey)
            throw new UnauthorizedAccessException("Restore the account archive before enrolling this device.");
        if (key.AccountIdentityKey is not null && account is null)
            throw new InvalidOperationException("Upload the encrypted recovery archive before registering device keys.");
        var session = await context.UserSessions.Include(s => s.CryptoSession)
            .ThenInclude(c => c.SignedPreKey).SingleOrDefaultAsync(s => s.Id == sessionId &&
                s.UserId == user && !s.IsRevoked && s.ExpiresAt > DateTime.UtcNow)
            ?? throw new UnauthorizedAccessException("Session is revoked or expired.");
        if (session.CryptoSession is not null)
        {
            var existing = ToDeviceKey(session.CryptoSession, user);
            if (existing.Fingerprint == key.Fingerprint)
            {
                if (existing.AccountIdentityKey is null && key.AccountIdentityKey is not null && key.Generation == 0)
                {
                    ApplyCertificate(session.CryptoSession, key);
                    await context.SaveChangesAsync();
                }
                else if (existing.Generation != key.Generation || existing.AccountIdentityKey != key.AccountIdentityKey)
                    throw new InvalidOperationException("Device generation mismatch.");
                return;
            }
            if (account is null || key.AccountIdentityKey != account.IdentityKey ||
                key.Generation != existing.Generation + 1 || key.PreviousFingerprint != existing.Fingerprint ||
                key.IdentityKey != existing.IdentityKey)
                throw new InvalidOperationException("A signed, consecutive device key rotation is required.");
            var rotating = session.CryptoSession;
            rotating.SignedPreKey.PublicSignedPreKey = Convert.FromBase64String(key.EncryptionKey);
            rotating.SignedPreKey.SignedPreKeySignature = Convert.FromBase64String(key.Signature);
            ApplyCertificate(rotating, key);
            await context.SaveChangesAsync();
            return;
        }
        if (key.Generation != 0 || key.PreviousFingerprint is not null)
            throw new InvalidOperationException("A newly enrolled device must start at generation zero.");
        var cryptoId = Guid.NewGuid();
        var crypto = new UserCryptoSession
        {
            Id = cryptoId, UserSessionId = sessionId,
            PublicIdentityKey = Convert.FromBase64String(key.IdentityKey),
            SignedPreKey = new SignedPreKey
            {
                Id = Guid.NewGuid(), UserCryptoSessionId = cryptoId,
                PublicSignedPreKey = Convert.FromBase64String(key.EncryptionKey),
                SignedPreKeySignature = Convert.FromBase64String(key.Signature)
            },
            OneTimePreKeys = new List<OneTimePreKey>()
        };
        context.UserCryptoSessions.Add(crypto);
        ApplyCertificate(crypto, key);
        session.CryptoSession = crypto;
        await context.SaveChangesAsync();
    }

    public async Task<RecoveryState> GetRecoveryAsync(Guid user)
    {
        var value = await context.E2eeRecoveryVaults.AsNoTracking().SingleOrDefaultAsync(v => v.UserId == user);
        return new(value is null ? null : new(user, value.Revision, value.IdentityKey, value.Ciphertext, value.Signature));
    }

    public async Task<Govor.Application.Authentication.AccountIdentityChange[]> GetIdentityChangesAsync(Guid user, Guid peer)
    {
        if (user != peer && !await context.PrivateChats.AnyAsync(c =>
                (c.UserAId == user && c.UserBId == peer) || (c.UserAId == peer && c.UserBId == user)) &&
            !await context.GroupMemberships.AnyAsync(m => m.UserId == user && !m.IsBanned &&
                (context.GroupMemberships.Any(p => p.GroupId == m.GroupId && p.UserId == peer && !p.IsBanned) ||
                 context.Messages.Any(msg => msg.RecipientType == RecipientType.Group && msg.RecipientId == m.GroupId && msg.SenderId == peer))))
            throw new UnauthorizedAccessException();
        return await context.E2eeIdentityChanges.AsNoTracking().Where(c => c.UserId == peer).OrderBy(c => c.Epoch)
            .Select(c => new Govor.Application.Authentication.AccountIdentityChange(c.UserId, c.Epoch,
                c.PreviousIdentityKey, c.IdentityKey, c.ChangedAt)).ToArrayAsync();
    }

    public async Task<RecoveryVault> SaveRecoveryAsync(Guid user, SaveRecoveryVault request)
    {
        if (request.ExpectedRevision < 0 || request.ExpectedRevision == long.MaxValue)
            throw new ArgumentException("Invalid archive revision.");
        var value = new RecoveryVault(user, request.ExpectedRevision + 1, request.IdentityKey, request.Ciphertext, request.Signature);
        E2eeRecoveryProtocol.Validate(value);
        var existing = await context.E2eeRecoveryVaults.SingleOrDefaultAsync(v => v.UserId == user);
        if (existing is null)
        {
            if (request.ExpectedRevision != 0) throw new InvalidOperationException("Archive revision conflict.");
            context.E2eeRecoveryVaults.Add(new E2eeRecoveryVault { UserId = user, Revision = value.Revision,
                IdentityKey = value.IdentityKey, Ciphertext = value.Ciphertext, Signature = value.Signature });
        }
        else
        {
            if (existing.IdentityKey != request.IdentityKey || existing.Revision != request.ExpectedRevision)
                throw new InvalidOperationException("Archive identity or revision conflict.");
            existing.Revision = value.Revision;
            existing.Ciphertext = value.Ciphertext;
            existing.Signature = value.Signature;
        }
        try { await context.SaveChangesAsync(); }
        catch (DbUpdateException) { context.ChangeTracker.Clear(); throw new InvalidOperationException("Archive revision conflict."); }
        return value;
    }

    private static void ApplyCertificate(UserCryptoSession crypto, DeviceKey key)
    {
        crypto.AccountIdentityKey = key.AccountIdentityKey;
        crypto.AccountSignature = key.AccountSignature;
        crypto.Generation = key.Generation;
        crypto.PreviousFingerprint = key.PreviousFingerprint;
    }

    public async Task<DeviceKey[]> GetOwnDevicesAsync(Guid user) =>
        (await context.UserCryptoSessions.AsNoTracking().Include(c => c.SignedPreKey).Include(c => c.UserSession)
            .Where(c => c.UserSession.UserId == user).ToListAsync()).Select(c => ToDeviceKey(c)).ToArray();

    public async Task CertifyHistoricalDeviceAsync(Guid user, DeviceKey key)
    {
        E2eeProtocol.ValidatePublicKey(key);
        E2eeRecoveryProtocol.VerifyCertificate(key);
        var root = await context.E2eeRecoveryVaults.AsNoTracking().SingleOrDefaultAsync(v => v.UserId == user);
        var crypto = await context.UserCryptoSessions.Include(c => c.SignedPreKey).Include(c => c.UserSession)
            .SingleOrDefaultAsync(c => c.UserSessionId == key.SessionId && c.UserSession.UserId == user);
        if (root is null || root.IdentityKey != key.AccountIdentityKey || key.UserId != user || crypto is null)
            throw new UnauthorizedAccessException("Only the account owner may certify historical device keys.");
        var stored = ToDeviceKey(crypto);
        if (stored.Fingerprint != key.Fingerprint || stored.Generation != key.Generation ||
            stored.PreviousFingerprint != key.PreviousFingerprint)
            throw new InvalidOperationException("Historical device keys cannot be changed.");
        ApplyCertificate(crypto, key);
        await context.SaveChangesAsync();
    }

    public async Task<DeviceKey?> GetHistoricalSignerAsync(Guid user, Guid conversation, RecipientType type, Guid session, string? identityKey = null)
    {
        if (!await context.HasChatAccessAsync(user, conversation, type)) throw new UnauthorizedAccessException();
        var crypto = await context.UserCryptoSessions.AsNoTracking().Include(c => c.SignedPreKey).Include(c => c.UserSession)
            .SingleOrDefaultAsync(c => c.UserSessionId == session);
        var owner = await context.UserSessions.AsNoTracking().Where(s => s.Id == session).Select(s => (Guid?)s.UserId).SingleOrDefaultAsync();
        if (owner is null || !await context.Messages.AnyAsync(m => m.RecipientId == conversation &&
                m.RecipientType == type && m.SenderId == owner.Value)) return null;
        if (crypto is not null)
        {
            var current = ToDeviceKey(crypto);
            if (identityKey is null || current.IdentityKey == identityKey) return current;
        }
        // A reset replaces this session's signing key, but v1 messages still need
        // the former account certificate. Public history never reactivates a key.
        var previous = await context.E2eeIdentityChanges.AsNoTracking().Where(c => c.UserId == owner.Value &&
                c.PreviousSessionId == session && c.PreviousDeviceKeyJson != null)
            .OrderByDescending(c => c.Epoch).Select(c => c.PreviousDeviceKeyJson!).ToArrayAsync();
        foreach (var json in previous)
        {
            var key = System.Text.Json.JsonSerializer.Deserialize<DeviceKey>(json, E2eeProtocol.Json);
            if (key is not null && key.SessionId == session && key.UserId == owner &&
                (identityKey is null || key.IdentityKey == identityKey)) return key;
        }
        return null;
    }

    public async Task<EncryptedMessage?> ValidateAsync(string content, Guid sender, Guid conversation,
        RecipientType type, Guid? replyTo, Guid[] media, Guid? editedMessage = null)
    {
        if (!E2eeProtocol.IsEncrypted(content))
        {
            if (await IsRequiredAsync(conversation, type))
                throw new ArgumentException("E2EE is required. Update the client and verify participant keys.");
            return null;
        }
        EncryptedMessage envelope;
        try { envelope = E2eeProtocol.Verify(content); }
        catch (Exception ex) when (ex is CryptographicException or FormatException or System.Text.Json.JsonException or
                                   Jose.JoseException or KeyNotFoundException or InvalidOperationException)
        { throw new ArgumentException("Invalid E2EE envelope."); }
        if (envelope.Sender.UserId != sender || envelope.ConversationId != conversation ||
            envelope.RecipientType != (int)type || envelope.ReplyToMessageId != replyTo ||
            envelope.IsEdit != editedMessage.HasValue ||
            (editedMessage.HasValue && envelope.MessageId != editedMessage) ||
            !envelope.MediaIds.Order().SequenceEqual(media.Order()) ||
            Math.Abs((double)envelope.CreatedAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) > 300_000)
            throw new ArgumentException("E2EE routing, attachment or time context does not match.");

        var signingSession = await context.UserSessions.AsNoTracking().Include(s => s.CryptoSession)
            .ThenInclude(c => c.SignedPreKey).SingleOrDefaultAsync(s => s.Id == envelope.Sender.SessionId &&
                s.UserId == sender && !s.IsRevoked && s.ExpiresAt > DateTime.UtcNow);
        if (signingSession?.CryptoSession is null ||
            ToDeviceKey(signingSession.CryptoSession, sender).Fingerprint != envelope.Sender.Fingerprint ||
            signingSession.CryptoSession.AccountIdentityKey != envelope.Sender.AccountIdentityKey ||
            signingSession.CryptoSession.Generation != envelope.Sender.Generation)
            throw new UnauthorizedAccessException("E2EE signing device is unknown or revoked.");
        var directory = await GetConversationKeysAsync(sender, conversation, type);
        var expected = directory.Devices.Select(k => k.Fingerprint).Distinct().Order(StringComparer.Ordinal);
        if (!expected.SequenceEqual(envelope.Recipients.Order(StringComparer.Ordinal)))
            throw new ArgumentException(E2eeProtocol.DevicesChangedError);
        return envelope;
    }

    private static DeviceKey ToDeviceKey(UserCryptoSession session, Guid? user = null) =>
        new(user ?? session.UserSession.UserId, session.UserSessionId,
            Convert.ToBase64String(session.PublicIdentityKey),
            Convert.ToBase64String(session.SignedPreKey.PublicSignedPreKey),
            Convert.ToBase64String(session.SignedPreKey.SignedPreKeySignature))
        {
            AccountIdentityKey = session.AccountIdentityKey, AccountSignature = session.AccountSignature,
            Generation = session.Generation, PreviousFingerprint = session.PreviousFingerprint
        };
}
