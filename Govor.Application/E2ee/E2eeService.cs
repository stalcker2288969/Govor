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
            .Where(c => members.Contains(c.UserSession.UserId) && !c.UserSession.IsRevoked)
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
        var session = await context.UserSessions.Include(s => s.CryptoSession)
            .ThenInclude(c => c.SignedPreKey).SingleOrDefaultAsync(s => s.Id == sessionId &&
                s.UserId == user && !s.IsRevoked && s.ExpiresAt > DateTime.UtcNow)
            ?? throw new UnauthorizedAccessException("Session is revoked or expired.");
        if (session.CryptoSession is not null)
        {
            var existing = ToDeviceKey(session.CryptoSession, user);
            if (existing.Fingerprint != key.Fingerprint)
                throw new InvalidOperationException("Session keys are immutable. Create a new session to change devices.");
            return;
        }
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
        session.CryptoSession = crypto;
        await context.SaveChangesAsync();
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
            ToDeviceKey(signingSession.CryptoSession, sender).Fingerprint != envelope.Sender.Fingerprint)
            throw new UnauthorizedAccessException("E2EE signing device is unknown or revoked.");
        var directory = await GetConversationKeysAsync(sender, conversation, type);
        var expected = directory.Devices.Select(k => k.Fingerprint).Distinct().Order(StringComparer.Ordinal);
        if (!expected.SequenceEqual(envelope.Recipients.Order(StringComparer.Ordinal)))
            throw new ArgumentException("Conversation devices changed. Reload and verify the recipient keys.");
        return envelope;
    }

    private static DeviceKey ToDeviceKey(UserCryptoSession session, Guid? user = null) =>
        new(user ?? session.UserSession.UserId, session.UserSessionId,
            Convert.ToBase64String(session.PublicIdentityKey),
            Convert.ToBase64String(session.SignedPreKey.PublicSignedPreKey),
            Convert.ToBase64String(session.SignedPreKey.SignedPreKeySignature));
}
