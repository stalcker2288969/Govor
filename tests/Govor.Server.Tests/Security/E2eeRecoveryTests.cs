using System.Security.Cryptography;
using Govor.Application.E2ee;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;

namespace Govor.Server.Tests;

[TestFixture]
public class E2eeRecoveryTests
{
    private TestDatabase _db = null!;
    private E2eeService _service = null!;
    private DevicePrivateKeys _keys = null!;
    private DevicePrivateKeys _bobKeys = null!;
    private string _identity = null!, _code = null!;
    [OneTimeSetUp]
    public void Keys()
    {
        _keys = E2eeProtocol.GenerateKeys();
        _bobKeys = E2eeProtocol.GenerateKeys();
        _identity = E2eeRecoveryProtocol.GenerateIdentity();
        _code = E2eeRecoveryProtocol.GenerateCode();
    }
    [SetUp] public void Setup() { _db = new(); _service = new(_db.Context); }
    [TearDown] public void Cleanup() => _db.Dispose();

    private async Task<RecoveryVault> Save(Guid user, long revision = 1, string? identity = null)
    {
        var vault = E2eeRecoveryProtocol.Seal(user, revision, identity ?? _identity, _code, "private archive");
        return await _service.SaveRecoveryAsync(user, new(revision - 1, vault.IdentityKey, vault.Ciphertext, vault.Signature));
    }
    private async Task<DeviceKey> Device(Guid user)
    {
        var session = new UserSession { UserId = user, RefreshTokenHash = Guid.NewGuid().ToString(), ExpiresAt = DateTime.UtcNow.AddDays(30) };
        _db.Context.UserSessions.Add(session);
        await _db.Context.SaveChangesAsync();
        return E2eeRecoveryProtocol.Certify(E2eeProtocol.PublicKey(user == _db.Bob ? _bobKeys : _keys, user, session.Id), _identity);
    }

    [Test]
    public async Task OnlyEncryptedArchiveIsStoredAndOtherAccountsCannotReadIt()
    {
        var saved = await Save(_db.Alice);
        Assert.That(E2eeRecoveryProtocol.Open(saved, _db.Alice, _code), Is.EqualTo("private archive"));
        var stored = await _db.Context.E2eeRecoveryVaults.SingleAsync();
        Assert.That(stored.Ciphertext, Does.Not.Contain("private archive").And.Not.Contain(_code).And.Not.Contain(_identity));
        Assert.That((await _service.GetRecoveryAsync(_db.Bob)).Vault, Is.Null);
        Assert.Throws<CryptographicException>(() => E2eeRecoveryProtocol.Open(saved, _db.Bob, _code));
        Assert.Catch(() => E2eeRecoveryProtocol.Open(saved, _db.Alice, E2eeRecoveryProtocol.GenerateCode()));
    }

    [Test]
    public async Task ArchiveCompareAndSwapRejectsStaleWritesAndIdentityReplacement()
    {
        await Save(_db.Alice);
        await Save(_db.Alice, 2);
        Assert.ThrowsAsync<InvalidOperationException>(() => Save(_db.Alice, 2));
        Assert.ThrowsAsync<InvalidOperationException>(() => Save(_db.Alice, 3, E2eeRecoveryProtocol.GenerateIdentity()));
        Assert.That((await _service.GetRecoveryAsync(_db.Alice)).Vault!.Revision, Is.EqualTo(2));
    }

    [Test]
    public async Task DeviceEnrollmentRequiresAccountCertificateAndCannotReuseItForAnotherSession()
    {
        await Save(_db.Alice);
        var device = await Device(_db.Alice);
        await _service.RegisterAsync(_db.Alice, device.SessionId, device);
        var second = await Device(_db.Alice);
        Assert.ThrowsAsync<CryptographicException>(() => _service.RegisterAsync(_db.Alice, second.SessionId,
            device with { SessionId = second.SessionId }));
        var wrongRoot = E2eeRecoveryProtocol.Certify(second, E2eeRecoveryProtocol.GenerateIdentity());
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RegisterAsync(_db.Alice, second.SessionId, wrongRoot));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RegisterAsync(_db.Alice, second.SessionId,
            E2eeProtocol.PublicKey(_keys, _db.Alice, second.SessionId)));
    }

    [Test]
    public async Task ConsecutiveRotationIsIdempotentAndOldRecipientKeysAreExcluded()
    {
        await Save(_db.Alice); await Save(_db.Bob);
        var a = await Device(_db.Alice); var b = await Device(_db.Bob);
        await _service.RegisterAsync(_db.Alice, a.SessionId, a);
        await _service.RegisterAsync(_db.Bob, b.SessionId, b);
        var before = await _service.GetConversationKeysAsync(_db.Alice, _db.ChatId, RecipientType.User);
        var oldContent = E2eeProtocol.Encrypt(new("before", []), _keys, a, before, Guid.NewGuid());
        var generated = E2eeProtocol.GenerateKeys();
        var rotatedPrivate = new DevicePrivateKeys(_bobKeys.IdentityKey, generated.EncryptionKey);
        var rotated = E2eeRecoveryProtocol.Certify(E2eeProtocol.PublicKey(rotatedPrivate, _db.Bob, b.SessionId), _identity, 1, b.Fingerprint);
        Assert.ThrowsAsync<InvalidOperationException>(() => _service.RegisterAsync(_db.Bob, b.SessionId,
            E2eeRecoveryProtocol.Certify(rotated, _identity, 2, b.Fingerprint)));
        await _service.RegisterAsync(_db.Bob, b.SessionId, rotated);
        await _service.RegisterAsync(_db.Bob, b.SessionId, rotated);
        var after = await _service.GetConversationKeysAsync(_db.Alice, _db.ChatId, RecipientType.User);
        Assert.That(after.Devices.Select(d => d.Fingerprint), Does.Not.Contain(b.Fingerprint));
        Assert.ThrowsAsync<ArgumentException>(() => _service.ValidateAsync(oldContent, _db.Alice, _db.ChatId, RecipientType.User, null, []));
        var encrypted = E2eeProtocol.Encrypt(new("after", []), _keys, a, after, Guid.NewGuid());
        Assert.That(E2eeProtocol.Decrypt(encrypted, rotatedPrivate, E2eeProtocol.Inspect(encrypted).MessageId,
            _db.ChatId, 0, _db.Alice).Text, Is.EqualTo("after"));
    }

    [Test]
    public async Task TamperedArchiveCannotBeSavedOrReassignedToAnotherAccount()
    {
        var saved = await Save(_db.Alice);
        Assert.ThrowsAsync<CryptographicException>(() => _service.SaveRecoveryAsync(_db.Bob,
            new(0, saved.IdentityKey, saved.Ciphertext, saved.Signature)));
        Assert.ThrowsAsync<CryptographicException>(() => _service.SaveRecoveryAsync(_db.Alice,
            new(1, saved.IdentityKey, saved.Ciphertext + "x", saved.Signature)));
    }

    [Test]
    public async Task LegacyDeviceCanUpgradeItsExistingKeyWithoutLosingHistory()
    {
        var device = await Device(_db.Alice);
        var legacy = E2eeProtocol.PublicKey(_keys, _db.Alice, device.SessionId);
        await _service.RegisterAsync(_db.Alice, device.SessionId, legacy);
        await Save(_db.Alice);
        await _service.RegisterAsync(_db.Alice, device.SessionId, device);
        var stored = await _db.Context.UserCryptoSessions.SingleAsync();
        Assert.That(stored.AccountIdentityKey, Is.EqualTo(device.AccountIdentityKey));
        Assert.That(Convert.ToBase64String(stored.PublicIdentityKey), Is.EqualTo(legacy.IdentityKey));
    }

    [Test]
    public async Task UncertifiedLegacySessionIsNotARecipientAfterAccountUpgrade()
    {
        var old = await Device(_db.Alice);
        await _service.RegisterAsync(_db.Alice, old.SessionId, E2eeProtocol.PublicKey(_keys, _db.Alice, old.SessionId));
        await Save(_db.Alice); await Save(_db.Bob);
        var current = await Device(_db.Alice); var bob = await Device(_db.Bob);
        await _service.RegisterAsync(_db.Alice, current.SessionId, current);
        await _service.RegisterAsync(_db.Bob, bob.SessionId, bob);
        var directory = await _service.GetConversationKeysAsync(_db.Alice, _db.ChatId, RecipientType.User);
        Assert.That(directory.Devices.Select(d => d.SessionId), Does.Not.Contain(old.SessionId));
        Assert.That(directory.Devices.Select(d => d.SessionId), Does.Contain(current.SessionId));
    }
}
