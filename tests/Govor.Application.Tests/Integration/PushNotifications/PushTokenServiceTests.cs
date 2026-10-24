using Govor.Application.PushNotifications;
using Govor.Application.Infrastructure.Common;
using Govor.Application.Tests.Support;
using Govor.Domain.Common;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Application.Tests.Integration.PushNotifications;

[TestFixture]
public class PushTokenServiceTests
{
    private TestDatabase _db = null!;
    private PushTokenService _service = null!;
    private readonly DateTime _now = new(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
    private INowDateTimeProvider Clock => Mock.Of<INowDateTimeProvider>(c => c.Now == _now);
    [SetUp] public void SetUp() { _db = new TestDatabase(); _service = new(_db.Context, Clock, NullLogger<PushTokenService>.Instance); }
    [TearDown] public void TearDown() => _db.Dispose();

    private async Task<Guid> Add(Guid user, string token, bool revoked = false, bool expired = false, bool active = true)
    {
        var session = new UserSession { UserId = user, RefreshTokenHash = token, ExpiresAt = _now.AddDays(expired ? -1 : 1), IsRevoked = revoked };
        _db.Context.UserSessions.Add(session);
        _db.Context.UserPushTokens.Add(new UserPushToken { UserId = user, UserSessionId = session.Id,
            Token = token, Platform = "android", IsActive = active });
        await _db.Context.SaveChangesAsync();
        return session.Id;
    }

    [Test]
    public async Task TokenQueriesFilterOwnerExpiryRevocationAndActivity()
    {
        var alice = await Add(_db.Alice, "alice");
        await Add(_db.Alice, "expired", expired: true);
        await Add(_db.Alice, "revoked", revoked: true);
        await Add(_db.Alice, "inactive", active: false);
        await Add(_db.Bob, "bob");
        Assert.That((await _service.GetStringsActiveTokensAsync(_db.Alice)).Value, Is.EqualTo(new[] { "alice" }));
        Assert.That((await _service.GetActiveTokenBySessionAsync(alice)).Value, Is.EqualTo("alice"));
        Assert.That((await _service.GetUsersStringsActiveTokensAsync(new[] { _db.Alice, _db.Bob, _db.Alice })).Value,
            Is.EquivalentTo(new[] { "alice", "bob" }));
        Assert.That((await _service.GetActiveTokenBySessionAsync(Guid.NewGuid())).Value, Is.Null);
    }

    [Test]
    public async Task DeactivationScopesAndRemovalPreserveUnrelatedTokens()
    {
        var first = await Add(_db.Alice, "first");
        await Add(_db.Alice, "second");
        await Add(_db.Bob, "bob");
        Assert.That((await _service.DeactivateTokenBySessionAsync(first)).IsSuccess, Is.True);
        Assert.That((await _service.GetStringsActiveTokensAsync(_db.Alice)).Value, Is.EqualTo(new[] { "second" }));
        Assert.That((await _service.DeactivateAllTokensByUserIdAsync(_db.Alice)).IsSuccess, Is.True);
        Assert.That((await _service.GetStringsActiveTokensAsync(_db.Alice)).Value, Is.Empty);
        Assert.That((await _service.GetStringsActiveTokensAsync(_db.Bob)).Value, Is.EqualTo(new[] { "bob" }));
        Assert.That((await _service.RemoveTokensAsync(new[] { "first", "second", "unknown" })).IsSuccess, Is.True);
        Assert.That(await _db.Context.UserPushTokens.AsNoTracking().Select(t => t.Token).ToListAsync(), Is.EqualTo(new[] { "bob" }));
        Assert.That((await _service.RemoveTokensAsync(Array.Empty<string>())).IsSuccess, Is.True);
        Assert.That((await _service.RemoveTokensAsync(null!)).IsSuccess, Is.True);
    }

    [Test]
    public async Task EveryDatabaseOperationReturnsFailureWhenItsCommandFails()
    {
        using var failed = _db.CreateContext(new FailDatabaseCommand());
        var service = new PushTokenService(failed, Clock, NullLogger<PushTokenService>.Instance);
        Assert.That((await service.DeactivateTokenBySessionAsync(Guid.NewGuid())).IsFailure, Is.True);
        Assert.That((await service.DeactivateAllTokensByUserIdAsync(_db.Alice)).IsFailure, Is.True);
        Assert.That((await service.GetStringsActiveTokensAsync(_db.Alice)).IsFailure, Is.True);
        Assert.That((await service.GetUsersStringsActiveTokensAsync(new[] { _db.Alice })).IsFailure, Is.True);
        Assert.That((await service.GetActiveTokenBySessionAsync(Guid.NewGuid())).IsFailure, Is.True);
        Assert.That((await service.RemoveTokensAsync(new[] { "token" })).IsFailure, Is.True);
    }

    [TestCase("empty")]
    [TestCase("long-token")]
    [TestCase("long-platform")]
    public async Task InvalidTokenArgumentsDoNotCreateRows(string kind)
    {
        var token = kind == "empty" ? "" : kind == "long-token" ? new string('t', 513) : "token";
        var platform = kind == "long-platform" ? new string('p', 51) : "android";
        Assert.That((await _service.AddOrUpdateTokenAsync(_db.Alice, Guid.NewGuid(), token, platform)).Error.Type, Is.EqualTo(ErrorType.Validation));
        Assert.That(await _db.Context.UserPushTokens.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task TwoExistingTokenRowsMergeWhenDeviceChangesAccount()
    {
        var alice = await Add(_db.Alice, "shared");
        var bob = await Add(_db.Bob, "old-bob");
        Assert.That((await _service.AddOrUpdateTokenAsync(_db.Bob, bob, "shared", "ios")).IsSuccess, Is.True);
        _db.Context.ChangeTracker.Clear();
        var token = await _db.Context.UserPushTokens.SingleAsync();
        Assert.That(token.UserId, Is.EqualTo(_db.Bob));
        Assert.That(token.UserSessionId, Is.EqualTo(bob));
        Assert.That(token.Token, Is.EqualTo("shared"));
        Assert.That(token.Platform, Is.EqualTo("ios"));
        Assert.That((await _service.GetActiveTokenBySessionAsync(alice)).Value, Is.Null);
    }
}

