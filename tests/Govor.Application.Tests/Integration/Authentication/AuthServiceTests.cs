using Govor.Application.Authentication;
using Govor.Application.Infrastructure.Common;
using Govor.Application.Infrastructure.Validators;
using Govor.Application.Users;
using Govor.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Govor.Application.Tests.Authentication;

[TestFixture]
public class AuthServiceTests
{
    private TestDatabase _db = null!;
    private AuthService _service = null!;
    private Mock<IPasswordHasher> _hasher = null!;
    private readonly DateTime _now = new(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        _hasher = new Mock<IPasswordHasher>();
        _hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("hashed-password");
        _hasher.Setup(h => h.Verify("correct", It.IsAny<string>())).Returns(true);
        var validator = new Mock<IUsernameValidator>();
        validator.Setup(v => v.Validate(It.IsAny<string>())).Returns(Result.Success());
        var clock = new Mock<INowDateTimeProvider>();
        clock.SetupGet(c => c.Now).Returns(_now);
        _service = new AuthService(_db.Context, new UserNameExistValidator(_db.Context),
            _hasher.Object, validator.Object, clock.Object);
    }

    [TearDown] public void TearDown() => _db.Dispose();

    [TestCase(false)]
    [TestCase(true)]
    public async Task RegistrationPersistsAccountReservesInvitationAndAssignsRole(bool admin)
    {
        var invite = await _db.Context.Invitations.SingleAsync();
        invite.IsAdmin = admin;
        await _db.Context.SaveChangesAsync();
        var result = await _service.RegistrationAsync("Новый", "secret", invite);
        Assert.That(result.IsSuccess, Is.True);
        _db.Context.ChangeTracker.Clear();
        var user = await _db.Context.Users.SingleAsync(u => u.Id == result.Value.Id);
        Assert.Multiple(() =>
        {
            Assert.That(user.Username, Is.EqualTo("Новый"));
            Assert.That(user.PasswordHash, Is.EqualTo("hashed-password"));
            Assert.That(user.CreatedOn, Is.EqualTo(DateOnly.FromDateTime(_now)));
            Assert.That(user.InviteId, Is.EqualTo(invite.Id));
        });
        Assert.That(await _db.Context.Admins.AnyAsync(a => a.UserId == user.Id), Is.EqualTo(admin));
        Assert.That((await _db.Context.Invitations.SingleAsync()).Participants, Is.EqualTo(1));
    }

    [Test]
    public async Task ExistingUsernameReturnsConflictWithoutConsumingInvitation()
    {
        var user = await _db.Context.Users.FirstAsync();
        var invite = await _db.Context.Invitations.SingleAsync();
        var result = await _service.RegistrationAsync(user.Username, "secret", invite);
        Assert.That(result.Error.Type, Is.EqualTo(ErrorType.Conflict));
        Assert.That((await _db.Context.Invitations.AsNoTracking().SingleAsync()).Participants, Is.Zero);
        _hasher.Verify(h => h.Hash(It.IsAny<string>()), Times.Never);
    }

    [TestCase("expired")]
    [TestCase("full")]
    [TestCase("inactive")]
    public async Task UnavailableInvitationDoesNotCreateAccount(string reason)
    {
        var invite = await _db.Context.Invitations.SingleAsync();
        if (reason == "expired") invite.EndDate = _now;
        if (reason == "full") invite.MaxParticipants = 3;
        if (reason == "inactive") invite.IsActive = false;
        await _db.Context.SaveChangesAsync();
        var result = await _service.RegistrationAsync("Новый", "secret", invite);
        Assert.That(result.Error.Code, Is.EqualTo("Auth.InvitationUnavailable"));
        Assert.That(await _db.Context.Users.CountAsync(), Is.EqualTo(3));
    }

    [TestCase("correct", true)]
    [TestCase("wrong", false)]
    public async Task LoginVerifiesStoredPassword(string password, bool success)
    {
        var user = await _db.Context.Users.FirstAsync();
        var result = await _service.LoginAsync(user.Username, password);
        Assert.That(result.IsSuccess, Is.EqualTo(success));
        _hasher.Verify(h => h.Verify(password, user.PasswordHash), Times.Once);
        if (success) Assert.That(result.Value.Id, Is.EqualTo(user.Id));
    }

    [Test]
    public async Task MissingUserReturnsNotFound()
    {
        var result = await _service.LoginAsync("missing", "secret");
        Assert.That(result.Error.Type, Is.EqualTo(ErrorType.NotFound));
        _hasher.Verify(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
