using Govor.Application.Users.UserSessions.Crypto;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Application.Tests.Integration.Users;

[TestFixture]
public class SessionCryptoTests
{
    private TestDatabase _db = null!;
    private SessionKeyAttacher _attacher = null!;
    private SessionKeysReader _reader = null!;
    private OneTimePreKeysRotator _rotator = null!;
    private Mock<ICurrentUserService> _current = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        _current = new Mock<ICurrentUserService>();
        _current.Setup(c => c.GetCurrentUserId()).Returns(_db.Alice);
        _attacher = new(NullLogger<SessionKeyAttacher>.Instance, _current.Object, _db.Context);
        _reader = new(NullLogger<SessionKeyAttacher>.Instance, _db.Context);
        _rotator = new(NullLogger<SessionKeyAttacher>.Instance, _current.Object, _db.Context);
    }
    [TearDown] public void TearDown() => _db.Dispose();

    private async Task<UserSession> Session(Guid? user = null, bool revoked = false, bool expired = false)
    {
        var session = new UserSession { UserId = user ?? _db.Alice, RefreshTokenHash = Guid.NewGuid().ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(expired ? -1 : 1), IsRevoked = revoked };
        _db.Context.UserSessions.Add(session);
        await _db.Context.SaveChangesAsync();
        return session;
    }
    private Task Attach(Guid id) => _attacher.AttachKeysAsync(id, new byte[] { 1 }, new byte[] { 2 },
        new byte[] { 3 }, new[] { new byte[] { 4 }, new byte[] { 5 } });

    [Test]
    public async Task AttachesIdentitySignedAndOneTimeKeysAndReadsThemFromDatabase()
    {
        var session = await Session();
        Assert.That(await _reader.HasKeysAttachedAsync(session.Id), Is.False);
        await Attach(session.Id);
        _db.Context.ChangeTracker.Clear();
        Assert.That(await _reader.HasKeysAttachedAsync(session.Id), Is.True);
        var keys = (await _reader.GetAllActiveKeysAsync(_db.Alice)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(keys.UserSessionId, Is.EqualTo(session.Id));
            Assert.That(keys.PublicIdentityKey, Is.EqualTo(new byte[] { 1 }));
            Assert.That(keys.SignedPreKey.PublicSignedPreKey, Is.EqualTo(new byte[] { 2 }));
            Assert.That(keys.SignedPreKey.SignedPreKeySignature, Is.EqualTo(new byte[] { 3 }));
            Assert.That(keys.OneTimePreKeys.Select(k => k.PublicKey[0]), Is.EquivalentTo(new byte[] { 4, 5 }));
        });
        Assert.That(await _reader.GetRemainingOneTimePreKeysCountAsync(session.Id), Is.EqualTo(2));
        var bySession = await _reader.GetKeysBySessionIdAsync(session.Id);
        Assert.That(bySession!.UserSession.Id, Is.EqualTo(session.Id));
        Assert.That(bySession.OneTimePreKeys.Count, Is.EqualTo(2));
        Assert.That(bySession.SignedPreKey, Is.Not.Null);
    }

    [Test]
    public async Task CannotReplaceAttachedKeysOrAttachToAnotherUsersSession()
    {
        var own = await Session();
        var other = await Session(_db.Bob);
        await Attach(own.Id);
        Assert.ThrowsAsync<InvalidOperationException>(() => Attach(own.Id));
        Assert.ThrowsAsync<InvalidOperationException>(() => Attach(other.Id));
        Assert.ThrowsAsync<ArgumentException>(() => Attach(Guid.NewGuid()));
        Assert.That(await _reader.HasKeysAttachedAsync(other.Id), Is.False);
    }

    [Test]
    public async Task ActiveKeysExcludeExpiredRevokedAndOtherUsersSessions()
    {
        var active = await Session();
        var expired = await Session(expired: true);
        var revoked = await Session(revoked: true);
        var bob = await Session(_db.Bob);
        foreach (var session in new[] { active, expired, revoked }) await Attach(session.Id);
        _current.Setup(c => c.GetCurrentUserId()).Returns(_db.Bob);
        await Attach(bob.Id);
        _db.Context.ChangeTracker.Clear();
        Assert.That((await _reader.GetAllActiveKeysAsync(_db.Alice)).Select(k => k.UserSessionId), Is.EqualTo(new[] { active.Id }));
        Assert.That((await _reader.GetAllActiveKeysAsync(_db.Bob)).Select(k => k.UserSessionId), Is.EqualTo(new[] { bob.Id }));
    }

    [Test]
    public async Task UsedKeyIsIdempotentAndRotationDeletesOnlyUsedKeys()
    {
        var session = await Session();
        await Attach(session.Id);
        var keys = await _db.Context.OneTimePreKeys.OrderBy(k => k.Id).ToListAsync();
        await _rotator.MarkOneTimePreKeyAsUsedAsync(session.Id, keys[0].Id);
        await _rotator.MarkOneTimePreKeyAsUsedAsync(session.Id, keys[0].Id);
        Assert.That(await _reader.GetRemainingOneTimePreKeysCountAsync(session.Id), Is.EqualTo(1));
        await _rotator.RotateOneTimePreKeysAsync(session.Id, new[] { new byte[] { 6 } });
        _db.Context.ChangeTracker.Clear();
        Assert.That(await _db.Context.OneTimePreKeys.AnyAsync(k => k.Id == keys[0].Id), Is.False);
        Assert.That(await _db.Context.OneTimePreKeys.AnyAsync(k => k.Id == keys[1].Id), Is.True);
        Assert.That(await _reader.GetRemainingOneTimePreKeysCountAsync(session.Id), Is.EqualTo(2));
        await _rotator.RotateOneTimePreKeysAsync(session.Id, Array.Empty<byte[]>());
        Assert.That(await _reader.GetRemainingOneTimePreKeysCountAsync(session.Id), Is.EqualTo(2));
    }

    [Test]
    public async Task AnotherUserCannotRotateOrConsumeKeysOfThisSession()
    {
        var session = await Session();
        await Attach(session.Id);
        var key = await _db.Context.OneTimePreKeys.FirstAsync();
        _current.Setup(c => c.GetCurrentUserId()).Returns(_db.Bob);
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _rotator.RotateOneTimePreKeysAsync(session.Id, new[] { new byte[] { 9 } }));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _rotator.MarkOneTimePreKeyAsUsedAsync(session.Id, key.Id));
        Assert.That(await _reader.GetRemainingOneTimePreKeysCountAsync(session.Id), Is.EqualTo(2));
    }

    [Test]
    public async Task MissingSessionAndMismatchedKeyAreRejected()
    {
        var session = await Session();
        await Attach(session.Id);
        var key = await _db.Context.OneTimePreKeys.FirstAsync();
        Assert.ThrowsAsync<ArgumentException>(() => _rotator.MarkOneTimePreKeyAsUsedAsync(Guid.NewGuid(), key.Id));
        Assert.ThrowsAsync<ArgumentException>(() => _rotator.MarkOneTimePreKeyAsUsedAsync(session.Id, Guid.NewGuid()));
        Assert.ThrowsAsync<ArgumentException>(() => _rotator.RotateOneTimePreKeysAsync(Guid.NewGuid(), Array.Empty<byte[]>()));
        Assert.That(await _reader.GetKeysBySessionIdAsync(Guid.NewGuid()), Is.Null);
        Assert.That(await _reader.GetRemainingOneTimePreKeysCountAsync(Guid.NewGuid()), Is.Zero);
    }
}

