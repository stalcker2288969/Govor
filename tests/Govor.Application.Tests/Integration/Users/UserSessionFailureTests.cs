using Govor.Application.Authentication.JWT;
using Govor.Application.Tests.Support;
using Govor.Application.PushNotifications;
using Govor.Domain.Common;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Govor.Application.Tests.Integration.Users;

[TestFixture]
public class UserSessionFailureTests
{
    [Test]
    public async Task ReaderAndRevokerReturnFailuresWithoutTouchingPushTokensOnDatabaseErrors()
    {
        using var db = new TestDatabase();
        var session = new UserSession { UserId = db.Alice, RefreshTokenHash = "hash", ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.Context.UserSessions.Add(session);
        await db.Context.SaveChangesAsync();
        using var failed = db.CreateContext(new FailDatabaseCommand());
        var push = new Mock<IPushTokenService>(MockBehavior.Strict);
        var reader = new UserSessionReader(failed, NullLogger<UserSessionReader>.Instance);
        var revoker = new UserSessionRevoker(failed, push.Object, NullLogger<UserSessionRevoker>.Instance);
        Assert.That((await reader.GetAllSessionsAsync(db.Alice)).IsFailure, Is.True);
        Assert.That((await revoker.CloseSessionByIdAsync(session.Id, db.Alice)).IsFailure, Is.True);
        Assert.That((await revoker.CloseAllSessionsAsync(db.Alice)).IsFailure, Is.True);
        push.VerifyNoOtherCalls();
        Assert.That((await db.Context.UserSessions.AsNoTracking().SingleAsync()).IsRevoked, Is.False);
    }

    [Test]
    public async Task OpeningSessionStopsAfterReaderFailure()
    {
        using var db = new TestDatabase();
        var reader = new Mock<IUserSessionReader>();
        var error = Error.ServerError("Sessions.Unavailable", "unavailable");
        reader.Setup(r => r.GetAllSessionsAsync(db.Alice)).ReturnsAsync(Result.Failure<List<UserSession>>(error));
        var jwt = new Mock<IJwtService>(MockBehavior.Strict);
        var opener = new UserSessionOpener(NullLogger<UserSessionOpener>.Instance, reader.Object, db.Context,
            Mock.Of<IJwtTokenHasher>(), Options.Create(new JwtRefreshOption()), jwt.Object);
        var result = await opener.OpenSessionAsync(await db.Context.Users.SingleAsync(u => u.Id == db.Alice), "phone");
        Assert.That(result.Error, Is.SameAs(error));
        jwt.VerifyNoOtherCalls();
        Assert.That(await db.Context.UserSessions.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task FailedSessionInsertReturnsFailureAndLeavesNoSession()
    {
        using var db = new TestDatabase();
        using var failed = db.CreateContext(new FailDatabaseCommand(sql => sql.Contains("INSERT INTO \"UserSessions\"", StringComparison.Ordinal)));
        var reader = new UserSessionReader(failed, NullLogger<UserSessionReader>.Instance);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateAccessTokenAsync(It.IsAny<User>(), It.IsAny<Guid>())).ReturnsAsync("access");
        jwt.Setup(j => j.GenerateRefreshTokenAsync(It.IsAny<User>())).ReturnsAsync("refresh");
        var hash = new Mock<IJwtTokenHasher>();
        hash.Setup(h => h.HashToken("refresh")).Returns("hash");
        var opener = new UserSessionOpener(NullLogger<UserSessionOpener>.Instance, reader, failed, hash.Object,
            Options.Create(new JwtRefreshOption { RefreshTokenLifetimeDays = 30 }), jwt.Object);
        Assert.That((await opener.OpenSessionAsync(await db.Context.Users.SingleAsync(u => u.Id == db.Alice), null!)).IsFailure, Is.True);
        Assert.That(await db.Context.UserSessions.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task RefreshDatabaseFailureReturnsFailure()
    {
        using var db = new TestDatabase();
        using var failed = db.CreateContext(new FailDatabaseCommand());
        var hash = Mock.Of<IJwtTokenHasher>(h => h.HashToken("token") == "hash");
        var service = new UserSessionRefresher(NullLogger<UserSessionRefresher>.Instance,
            Options.Create(new JwtRefreshOption()), hash, Mock.Of<IJwtService>(), failed);
        Assert.That((await service.RefreshTokenAsync("token")).IsFailure, Is.True);
    }

    [Test]
    public async Task RevocationBetweenTokenLookupAndRotationRejectsRefresh()
    {
        using var db = new TestDatabase();
        var session = new UserSession { UserId = db.Alice, RefreshTokenHash = "old-hash", ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.Context.UserSessions.Add(session);
        await db.Context.SaveChangesAsync();
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateAccessTokenAsync(It.IsAny<User>(), session.Id)).ReturnsAsync("access");
        jwt.Setup(j => j.GenerateRefreshTokenAsync(It.IsAny<User>())).Returns(async () =>
        {
            await db.Context.UserSessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(s => s.SetProperty(s => s.IsRevoked, true));
            return "new";
        });
        var hash = new Mock<IJwtTokenHasher>();
        hash.Setup(h => h.HashToken("old")).Returns("old-hash");
        hash.Setup(h => h.HashToken("new")).Returns("new-hash");
        using var context = db.CreateContext();
        var service = new UserSessionRefresher(NullLogger<UserSessionRefresher>.Instance,
            Options.Create(new JwtRefreshOption { RefreshTokenLifetimeDays = 30 }), hash.Object, jwt.Object, context);
        var result = await service.RefreshTokenAsync("old");
        Assert.That(result.Error.Type, Is.EqualTo(ErrorType.Unauthorized));
        var stored = await db.Context.UserSessions.AsNoTracking().SingleAsync();
        Assert.That(stored.RefreshTokenHash, Is.EqualTo("old-hash"));
        Assert.That(stored.IsRevoked, Is.True);
    }
}

