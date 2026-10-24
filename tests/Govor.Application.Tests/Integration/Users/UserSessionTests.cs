using Govor.Application.Authentication.JWT;
using Govor.Application.Users.UserSessions;
using Govor.Application.PushNotifications;
using Govor.Domain.Common;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Govor.Application.Tests.Users;

[TestFixture]
public class UserSessionTests
{
    private TestDatabase _db = null!;
    private Mock<IJwtService> _jwt = null!;
    private Mock<IJwtTokenHasher> _hasher = null!;
    private UserSessionReader _reader = null!;
    private UserSessionRefresher _refresher = null!;
    private UserSessionOpener _opener = null!;
    private Mock<IPushTokenService> _push = null!;
    private UserSessionRevoker _revoker = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        _jwt = new Mock<IJwtService>();
        _jwt.Setup(j => j.GenerateAccessTokenAsync(It.IsAny<User>(), It.IsAny<Guid>()))
            .ReturnsAsync((User _, Guid sid) => "access-" + sid);
        _jwt.Setup(j => j.GenerateRefreshTokenAsync(It.IsAny<User>())).ReturnsAsync(() => Guid.NewGuid().ToString());
        _hasher = new Mock<IJwtTokenHasher>();
        _hasher.Setup(h => h.HashToken(It.IsAny<string>())).Returns((string token) => "hash-" + token);
        _reader = new UserSessionReader(_db.Context, NullLogger<UserSessionReader>.Instance);
        var options = Options.Create(new JwtRefreshOption { RefreshTokenLifetimeDays = 30 });
        _opener = new UserSessionOpener(NullLogger<UserSessionOpener>.Instance, _reader, _db.Context,
            _hasher.Object, options, _jwt.Object);
        _refresher = new UserSessionRefresher(NullLogger<UserSessionRefresher>.Instance, options,
            _hasher.Object, _jwt.Object, _db.Context);
        _push = new Mock<IPushTokenService>();
        _push.Setup(p => p.DeactivateTokenBySessionAsync(It.IsAny<Guid>())).ReturnsAsync(Result.Success());
        _push.Setup(p => p.DeactivateAllTokensByUserIdAsync(It.IsAny<Guid>())).ReturnsAsync(Result.Success());
        _revoker = new UserSessionRevoker(_db.Context, _push.Object, NullLogger<UserSessionRevoker>.Instance);
    }
    [TearDown] public void TearDown() => _db.Dispose();

    private async Task<UserSession> AddSession(Guid user, bool revoked = false, bool expired = false)
    {
        var session = new UserSession { UserId = user, RefreshTokenHash = "hash-" + Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(expired ? -1 : 1), IsRevoked = revoked, DeviceInfo = "phone" };
        _db.Context.UserSessions.Add(session);
        await _db.Context.SaveChangesAsync();
        return session;
    }

    [Test]
    public async Task SameDeviceLabelStillCreatesDistinctSessionsAndStoresOnlyTokenHashes()
    {
        var user = await _db.Context.Users.SingleAsync(u => u.Id == _db.Alice);
        var first = await _opener.OpenSessionAsync(user, "phone");
        var second = await _opener.OpenSessionAsync(user, "phone");
        Assert.That(first.IsSuccess && second.IsSuccess, Is.True);
        var sessions = await _db.Context.UserSessions.AsNoTracking().ToListAsync();
        Assert.That(sessions, Has.Count.EqualTo(2));
        Assert.That(sessions.Select(s => s.Id).Distinct().Count(), Is.EqualTo(2));
        Assert.That(sessions.Select(s => s.RefreshTokenHash), Is.EquivalentTo(
            new[] { "hash-" + first.Value.refreshToken, "hash-" + second.Value.refreshToken }));
        Assert.That(sessions.All(s => s.ExpiresAt > DateTime.UtcNow.AddDays(29)), Is.True);
    }

    [Test]
    public async Task ReaderFiltersOwnerAndRevocation()
    {
        var own = await AddSession(_db.Alice);
        await AddSession(_db.Alice, revoked: true);
        await AddSession(_db.Bob);
        Assert.That((await _reader.GetAllSessionsAsync(_db.Alice)).Value.Select(s => s.Id), Is.EqualTo(new[] { own.Id }));
        Assert.That((await _reader.GetAllSessionsAsync(Guid.Empty)).IsFailure, Is.True);
    }

    [Test]
    public async Task RefreshRotatesHashAndRejectsReplay()
    {
        var session = await AddSession(_db.Alice);
        var oldToken = session.RefreshTokenHash["hash-".Length..];
        var result = await _refresher.RefreshTokenAsync(oldToken);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value.accessToken, Is.EqualTo("access-" + session.Id));
        _db.Context.ChangeTracker.Clear();
        Assert.That((await _db.Context.UserSessions.SingleAsync()).RefreshTokenHash,
            Is.EqualTo("hash-" + result.Value.refreshToken));
        Assert.That((await _refresher.RefreshTokenAsync(oldToken)).IsFailure, Is.True);
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task RevokedOrExpiredSessionCannotRefresh(bool revoked, bool expired)
    {
        var session = await AddSession(_db.Alice, revoked, expired);
        Assert.That((await _refresher.RefreshTokenAsync(session.RefreshTokenHash["hash-".Length..])).IsFailure, Is.True);
        _jwt.Verify(j => j.GenerateRefreshTokenAsync(It.IsAny<User>()), Times.Never);
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("unknown")]
    public async Task InvalidRefreshTokenReturnsFailure(string token) =>
        Assert.That((await _refresher.RefreshTokenAsync(token)).IsFailure, Is.True);

    [Test]
    public async Task SessionCanBeRevokedOnlyByOwnerAndPushTokenIsDeactivated()
    {
        var session = await AddSession(_db.Alice);
        Assert.That((await _revoker.CloseSessionByIdAsync(session.Id, _db.Bob)).Error.Type, Is.EqualTo(ErrorType.NotFound));
        _push.VerifyNoOtherCalls();
        Assert.That((await _revoker.CloseSessionByIdAsync(session.Id, _db.Alice)).IsSuccess, Is.True);
        Assert.That((await _db.Context.UserSessions.AsNoTracking().SingleAsync()).IsRevoked, Is.True);
        _push.Verify(p => p.DeactivateTokenBySessionAsync(session.Id), Times.Once);
    }

    [Test]
    public async Task CloseAllIsIdempotentAndKeepsOtherUsersSessions()
    {
        await AddSession(_db.Alice);
        var bob = await AddSession(_db.Bob);
        Assert.That((await _revoker.CloseAllSessionsAsync(_db.Alice)).IsSuccess, Is.True);
        Assert.That((await _revoker.CloseAllSessionsAsync(_db.Alice)).IsSuccess, Is.True);
        Assert.That((await _db.Context.UserSessions.AsNoTracking().SingleAsync(s => s.Id == bob.Id)).IsRevoked, Is.False);
        _push.Verify(p => p.DeactivateAllTokensByUserIdAsync(_db.Alice), Times.Once);
    }
}
