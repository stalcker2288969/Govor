using System.Security.Cryptography;
using System.Text;
using Govor.Application.E2ee;
using Govor.Application.Messages;
using Govor.Application.Messages.Parameters;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models.Users;
using Govor.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Govor.Server.Tests;

[TestFixture]
public class E2eeTests
{
    private DevicePrivateKeys _alice = null!, _bob = null!, _other = null!;
    private TestDatabase _db = null!;
    private E2eeService _e2ee = null!;
    private DeviceKey _a = null!, _b = null!;

    [OneTimeSetUp]
    public void Keys() { _alice = E2eeProtocol.GenerateKeys(); _bob = E2eeProtocol.GenerateKeys(); _other = E2eeProtocol.GenerateKeys(); }

    [SetUp]
    public async Task SetUp()
    {
        _db = new TestDatabase();
        _e2ee = new E2eeService(_db.Context);
        _a = await Register(_db.Alice, _alice);
        _b = await Register(_db.Bob, _bob);
    }

    [TearDown] public void Cleanup() => _db.Dispose();

    private async Task<DeviceKey> Register(Guid user, DevicePrivateKeys keys)
    {
        var session = new UserSession { UserId = user, RefreshTokenHash = Guid.NewGuid().ToString(), ExpiresAt = DateTime.UtcNow.AddDays(30) };
        _db.Context.UserSessions.Add(session);
        await _db.Context.SaveChangesAsync();
        var key = E2eeProtocol.PublicKey(keys, user, session.Id);
        await _e2ee.RegisterAsync(user, session.Id, key);
        return key;
    }

    private async Task<string> Encrypt(Guid? id = null, bool edit = false, Guid? conversation = null, int type = 0,
        AttachmentSecret[]? attachments = null) => E2eeProtocol.Encrypt(new("секретное сообщение", attachments ?? []),
        _alice, _a, await _e2ee.GetConversationKeysAsync(_db.Alice, conversation ?? _db.ChatId, (RecipientType)type),
        id ?? Guid.NewGuid(), isEdit: edit);

    [Test]
    public async Task BothParticipantsCanDecryptButServerAndOutsiderCannot()
    {
        var ciphertext = await Encrypt();
        var envelope = E2eeProtocol.Verify(ciphertext);
        foreach (var keys in new[] { _alice, _bob })
            Assert.That(E2eeProtocol.Decrypt(ciphertext, keys, envelope.MessageId, _db.ChatId, 0, _db.Alice).Text,
                Is.EqualTo("секретное сообщение"));
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Decrypt(ciphertext, _other,
            envelope.MessageId, _db.ChatId, 0, _db.Alice));
        Assert.That(ciphertext, Does.Not.Contain("секретное сообщение"));
    }

    [Test]
    public async Task ServerPersistsOnlyCiphertextAndRetriesAreIdempotent()
    {
        var ciphertext = await Encrypt();
        var sender = new MessageSendingService(_db.Context, NullLogger<MessageSendingService>.Instance);
        var request = new SendMessage(ciphertext, null, _db.ChatId, RecipientType.User, _db.Alice, DateTime.UtcNow, []);
        Assert.That((await sender.SendMessageAsync(request)).IsSuccess, Is.True);
        Assert.That((await sender.SendMessageAsync(request)).IsSuccess, Is.True);
        Assert.That(await _db.Context.Messages.CountAsync(), Is.EqualTo(1));
        Assert.That((await _db.Context.Messages.SingleAsync()).EncryptedContent, Is.EqualTo(ciphertext));
        Assert.That(await _db.Context.ChatPushNotifications.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task PlaintextIsRejectedForPrivateChats()
    {
        var sender = new MessageSendingService(_db.Context, NullLogger<MessageSendingService>.Instance);
        var result = await sender.SendMessageAsync(new("hello", null, _db.ChatId, RecipientType.User,
            _db.Alice, DateTime.UtcNow, []));
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(await _db.Context.Messages.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task NewDeviceMakesOldRecipientManifestInvalid()
    {
        var ciphertext = await Encrypt();
        await Register(_db.Bob, _other);
        Assert.ThrowsAsync<ArgumentException>(() => _e2ee.ValidateAsync(ciphertext,
            _db.Alice, _db.ChatId, RecipientType.User, null, []));
    }

    [Test]
    public async Task RevokedSigningDeviceCannotSend()
    {
        var ciphertext = await Encrypt();
        await _db.Context.UserSessions.Where(s => s.Id == _a.SessionId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.IsRevoked, true));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _e2ee.ValidateAsync(ciphertext,
            _db.Alice, _db.ChatId, RecipientType.User, null, []));
    }

    [Test]
    public async Task ExpiredOfflineDeviceStillReceivesFutureMessages()
    {
        await _db.Context.UserSessions.Where(s => s.Id == _b.SessionId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ExpiresAt, DateTime.UtcNow.AddDays(-1)));
        var ciphertext = await Encrypt();
        var envelope = E2eeProtocol.Verify(ciphertext);
        Assert.That(E2eeProtocol.Decrypt(ciphertext, _bob, envelope.MessageId, _db.ChatId, 0, _db.Alice).Text,
            Is.EqualTo("секретное сообщение"));
    }

    [Test]
    public async Task KeysAreIdempotentImmutableAndOwnedBySession()
    {
        await _e2ee.RegisterAsync(_db.Alice, _a.SessionId, _a);
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _e2ee.RegisterAsync(_db.Bob, _a.SessionId, _a));
        var replacement = E2eeProtocol.PublicKey(_other, _db.Alice, _a.SessionId);
        Assert.ThrowsAsync<InvalidOperationException>(() => _e2ee.RegisterAsync(_db.Alice, _a.SessionId, replacement));
    }

    [Test]
    public async Task OutsiderCannotRetrieveConversationKeys() =>
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _e2ee.GetConversationKeysAsync(_db.Outsider, _db.ChatId, RecipientType.User));

    [Test]
    public async Task InvalidKeyBindingIsRejected()
    {
        var invalid = _a with { EncryptionKey = _b.EncryptionKey };
        Assert.Throws<CryptographicException>(() => E2eeProtocol.ValidatePublicKey(invalid));
        Assert.ThrowsAsync<CryptographicException>(() => _e2ee.RegisterAsync(_db.Alice, _a.SessionId, invalid));
    }

    [Test]
    public async Task SignedMessageCannotBeMovedToAnotherAuthorChatReplyOrId()
    {
        var ciphertext = await Encrypt();
        var envelope = E2eeProtocol.Verify(ciphertext);
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Decrypt(ciphertext, _bob, Guid.NewGuid(), _db.ChatId, 0, _db.Alice));
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Decrypt(ciphertext, _bob, envelope.MessageId, Guid.NewGuid(), 0, _db.Alice));
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Decrypt(ciphertext, _bob, envelope.MessageId, _db.ChatId, 0, _db.Outsider));
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Decrypt(ciphertext, _bob, envelope.MessageId, _db.ChatId, 0, _db.Alice, Guid.NewGuid()));
    }

    [Test]
    public async Task TamperingWithSignedPayloadFails()
    {
        var ciphertext = await Encrypt();
        var parts = ciphertext[E2eeProtocol.Prefix.Length..].Split('.');
        var json = Encoding.UTF8.GetString(Jose.Base64Url.Decode(parts[1]));
        parts[1] = Jose.Base64Url.Encode(Encoding.UTF8.GetBytes(json.Replace(_db.Alice.ToString(), _db.Outsider.ToString())));
        Assert.Catch(() => E2eeProtocol.Verify(E2eeProtocol.Prefix + string.Join('.', parts)));
    }

    [Test]
    public async Task ExcludedMemberCannotDecryptNewGroupMessagesAndStaleSendIsRejected()
    {
        var group = new ChatGroup { Id = Guid.NewGuid(), Name = "private", Description = "", IsPrivate = true };
        _db.Context.ChatGroups.Add(group);
        foreach (var user in new[] { _db.Alice, _db.Bob })
            _db.Context.GroupMemberships.Add(new GroupMembership { Id = Guid.NewGuid(), GroupId = group.Id, UserId = user });
        await _db.Context.SaveChangesAsync();
        var old = await Encrypt(conversation: group.Id, type: 1);
        await _db.Context.GroupMemberships.Where(m => m.GroupId == group.Id && m.UserId == _db.Bob)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.IsBanned, true));
        Assert.ThrowsAsync<ArgumentException>(() => _e2ee.ValidateAsync(old, _db.Alice, group.Id, RecipientType.Group, null, []));
        var current = await Encrypt(conversation: group.Id, type: 1);
        Assert.Catch(() => E2eeProtocol.Decrypt(current, _bob, E2eeProtocol.Inspect(current).MessageId, group.Id, 1, _db.Alice));
    }

    [Test]
    public void AttachmentIsAuthenticatedAndWrongKeyOrModifiedDataFails()
    {
        var original = Encoding.UTF8.GetBytes("private attachment bytes");
        var (encrypted, key) = E2eeProtocol.EncryptAttachment(original);
        Assert.That(E2eeProtocol.DecryptAttachment(encrypted, key), Is.EqualTo(original));
        Assert.Catch(() => E2eeProtocol.DecryptAttachment(encrypted, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        encrypted[15] ^= 1;
        Assert.Catch(() => E2eeProtocol.DecryptAttachment(encrypted, key));
    }

    [Test]
    public async Task AttachmentKeyExistsOnlyInsideEncryptedPayload()
    {
        var (_, key) = E2eeProtocol.EncryptAttachment("secret"u8.ToArray());
        var media = new AttachmentSecret(Guid.NewGuid(), key, "secret.png", "image/png");
        var ciphertext = await Encrypt(attachments: [media]);
        Assert.That(ciphertext, Does.Not.Contain(key).And.Not.Contain("secret.png"));
        var plaintext = E2eeProtocol.Decrypt(ciphertext, _bob, E2eeProtocol.Inspect(ciphertext).MessageId, _db.ChatId, 0, _db.Alice);
        Assert.That(plaintext.Attachments.Single(), Is.EqualTo(media));
    }

    [Test]
    public async Task EncryptedEditIsAcceptedButPlaintextAndOlderEditsAreRejected()
    {
        var first = await Encrypt();
        var sender = new MessageSendingService(_db.Context, NullLogger<MessageSendingService>.Instance);
        var sent = await sender.SendMessageAsync(new(first, null, _db.ChatId, RecipientType.User, _db.Alice, DateTime.UtcNow, []));
        var editor = new MessageEditingService(_db.Context, NullLogger<MessageEditingService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new MessageEditingOptions { Enabled = false }));
        var directory = await _e2ee.GetConversationKeysAsync(_db.Alice, _db.ChatId, RecipientType.User);
        var edited = E2eeProtocol.Encrypt(new("edited secret", []), _alice, _a, directory, sent.Message.Id,
            isEdit: true, createdAt: E2eeProtocol.Inspect(first).CreatedAt + 1);
        var result = await editor.EditMessageAsync(new(_db.Alice, sent.Message.Id, edited, DateTime.UtcNow));
        Assert.That(result.IsSuccess, Is.True);
        var plaintext = await editor.EditMessageAsync(new(_db.Alice, sent.Message.Id, "leak", DateTime.UtcNow));
        Assert.That(plaintext.IsSuccess, Is.False);
        var replay = await editor.EditMessageAsync(new(_db.Alice, sent.Message.Id, edited, DateTime.UtcNow));
        Assert.That(replay.IsSuccess, Is.False);
        Assert.That((await _db.Context.Messages.SingleAsync()).EncryptedContent, Is.EqualTo(edited));
    }

    [Test]
    public async Task PlaintextIsRejectedForPrivateGroupsAndChannels()
    {
        foreach (var channel in new[] { false, true })
        {
            var group = new ChatGroup { Id = Guid.NewGuid(), Name = "private", Description = "",
                IsPrivate = true, IsChannel = channel, OwnerUserId = _db.Alice };
            _db.Context.ChatGroups.Add(group);
            _db.Context.GroupMemberships.Add(new GroupMembership { Id = Guid.NewGuid(), GroupId = group.Id, UserId = _db.Alice });
            await _db.Context.SaveChangesAsync();
            var sender = new MessageSendingService(_db.Context, NullLogger<MessageSendingService>.Instance);
            Assert.That((await sender.SendMessageAsync(new("plaintext", null, group.Id,
                RecipientType.Group, _db.Alice, DateTime.UtcNow, []))).IsSuccess, Is.False);
        }
        Assert.That(await _db.Context.Messages.CountAsync(), Is.Zero);
    }
}
