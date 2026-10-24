using Govor.Application.Infrastructure.AdminsStuff;
using Govor.Application.Infrastructure.Common;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Application.Tests.Integration.Administration;

[TestFixture]
public class AdministrationTests
{
    private TestDatabase _db = null!;
    private UsersService _users = null!;
    private InvitationGetter _invitations = null!;
    private Mock<IPasswordHasher> _hasher = null!;
    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        _hasher = new Mock<IPasswordHasher>();
        _hasher.Setup(h => h.Hash("new password")).Returns("new hash");
        _users = new(_db.Context, _hasher.Object);
        _invitations = new(NullLogger<InvitationGetter>.Instance, _db.Context);
    }
    [TearDown] public void TearDown() => _db.Dispose();

    [Test]
    public async Task PasswordResetRevokesOnlyTargetsSessionsAndPushTokens()
    {
        var own = new UserSession { UserId = _db.Alice, RefreshTokenHash = "alice" };
        var bob = new UserSession { UserId = _db.Bob, RefreshTokenHash = "bob" };
        _db.Context.UserSessions.AddRange(own, bob);
        _db.Context.UserPushTokens.AddRange(
            new UserPushToken { UserId = _db.Alice, UserSessionId = own.Id, Token = "alice", Platform = "android" },
            new UserPushToken { UserId = _db.Bob, UserSessionId = bob.Id, Token = "bob", Platform = "android" });
        await _db.Context.SaveChangesAsync();
        await _users.SetPasswordAsync(_db.Alice, "new password");
        _db.Context.ChangeTracker.Clear();
        Assert.That((await _users.GetUserById(_db.Alice)).PasswordHash, Is.EqualTo("new hash"));
        Assert.That((await _db.Context.UserSessions.SingleAsync(s => s.Id == own.Id)).IsRevoked, Is.True);
        Assert.That((await _db.Context.UserSessions.SingleAsync(s => s.Id == bob.Id)).IsRevoked, Is.False);
        Assert.That((await _db.Context.UserPushTokens.SingleAsync(t => t.UserId == _db.Alice)).IsActive, Is.False);
        Assert.That((await _db.Context.UserPushTokens.SingleAsync(t => t.UserId == _db.Bob)).IsActive, Is.True);
    }

    [Test]
    public async Task MissingUserDoesNotHashOrChangeAccounts()
    {
        var missing = Guid.NewGuid();
        Assert.That(await _users.GetUserById(missing), Is.Null);
        await _users.SetPasswordAsync(missing, "new password");
        _hasher.VerifyNoOtherCalls();
        Assert.That(await _db.Context.Users.CountAsync(), Is.EqualTo(3));
    }

    [Test]
    public async Task UserListIsBoundedAndUntracked()
    {
        var invite = await _db.Context.Invitations.SingleAsync();
        _db.Context.Users.AddRange(Enumerable.Range(0, 60).Select(i => new User {
            Id = Guid.NewGuid(), Username = "user-" + i, PasswordHash = "hash", InviteId = invite.Id, Description = "" }));
        await _db.Context.SaveChangesAsync();
        _db.Context.ChangeTracker.Clear();
        Assert.That(await _users.GetAllUsersAsync(), Has.Count.EqualTo(50));
        Assert.That(_db.Context.ChangeTracker.Entries(), Is.Empty);
    }

    [Test]
    public async Task InvitationListFiltersInactiveButLookupCanReadIt()
    {
        var inactive = new Invitation { Id = Guid.NewGuid(), Code = "inactive", Description = "", IsActive = false };
        _db.Context.Invitations.Add(inactive);
        await _db.Context.SaveChangesAsync();
        _db.Context.ChangeTracker.Clear();
        Assert.That((await _invitations.GetAllAsync()).Any(i => i.Id == inactive.Id), Is.False);
        Assert.That((await _invitations.FindByIdAsync(inactive.Id)).Value.Code, Is.EqualTo("inactive"));
        Assert.That((await _invitations.FindByIdAsync(Guid.NewGuid())).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That(_db.Context.ChangeTracker.Entries(), Is.Empty);
    }

    [Test]
    public async Task RolesDefaultToUserAndAdminRoleComesFromInvitation()
    {
        var clock = Mock.Of<INowDateTimeProvider>();
        var invites = new InvitesService(_db.Context, clock);
        var user = await _users.GetUserById(_db.Alice);
        Assert.That(await invites.GetRoleNameAsync(Guid.NewGuid()), Is.EqualTo("User"));
        Assert.That(await invites.GetRoleNameAsync(user), Is.EqualTo("User"));
        var invitation = await _db.Context.Invitations.SingleAsync();
        invitation.IsAdmin = true;
        await _db.Context.SaveChangesAsync();
        Assert.That(await invites.GetRoleNameAsync(user), Is.EqualTo("Admin"));
    }

    [Test]
    public async Task UnknownInvitationReturnsNotFound()
    {
        var service = new InvitesService(_db.Context, Mock.Of<INowDateTimeProvider>());
        Assert.That((await service.ValidateAsync("unknown")).Error.Type, Is.EqualTo(ErrorType.NotFound));
    }
}

