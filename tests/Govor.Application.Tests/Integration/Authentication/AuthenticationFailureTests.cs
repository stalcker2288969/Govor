using Govor.Application.Infrastructure.Common;
using Govor.Application.Users;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Govor.Application.Tests.Integration.Authentication;

[TestFixture]
public class AuthenticationFailureTests
{
    [Test]
    public async Task InvalidUsernameStopsRegistrationBeforePasswordHashOrDatabaseWrite()
    {
        using var db = new TestDatabase();
        var error = Error.Validation("Username.Invalid", "invalid username");
        var validator = new Mock<IUsernameValidator>();
        validator.Setup(v => v.Validate("bad")).Returns(Result.Failure(error));
        var hasher = new Mock<IPasswordHasher>(MockBehavior.Strict);
        var exists = new Mock<IUserNameExistValidator>(MockBehavior.Strict);
        var service = new AuthService(db.Context, exists.Object, hasher.Object, validator.Object, Mock.Of<INowDateTimeProvider>());
        var result = await service.RegistrationAsync("bad", "password", await db.Context.Invitations.SingleAsync());
        Assert.That(result.Error, Is.SameAs(error));
        Assert.That(await db.Context.Users.CountAsync(), Is.EqualTo(3));
        hasher.VerifyNoOtherCalls();
        exists.VerifyNoOtherCalls();
    }

    [Test]
    public async Task NonChannelRequiredGroupRejectsRegistrationWithoutConsumingInvitation()
    {
        using var db = new TestDatabase();
        var group = new ChatGroup { Id = Guid.NewGuid(), Name = "not a channel", Description = "" };
        db.Context.ChatGroups.Add(group);
        (await db.Context.ServerCommunitySettings.SingleAsync()).RequiredChannelId = group.Id;
        await db.Context.SaveChangesAsync();
        var validator = Mock.Of<IUsernameValidator>(v => v.Validate("Новый") == Result.Success());
        var hasher = Mock.Of<IPasswordHasher>(h => h.Hash("password") == "hash");
        var service = new AuthService(db.Context, new UserNameExistValidator(db.Context), hasher, validator,
            Mock.Of<INowDateTimeProvider>(c => c.Now == DateTime.UtcNow.Date));
        var invitation = await db.Context.Invitations.SingleAsync();
        var result = await service.RegistrationAsync("Новый", "password", invitation);
        Assert.That(result.Error.Code, Is.EqualTo("Auth.RequiredChannelUnavailable"));
        Assert.That(await db.Context.Users.CountAsync(), Is.EqualTo(3));
        Assert.That((await db.Context.Invitations.AsNoTracking().SingleAsync()).Participants, Is.Zero);
    }
}

