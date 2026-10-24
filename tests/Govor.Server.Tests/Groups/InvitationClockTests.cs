using Govor.Application.Authentication;
using Govor.Application.Infrastructure.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Govor.Server.Tests;

[TestFixture]
public class InvitationClockTests
{
    [TestCase(-1, false)]
    [TestCase(0, false)]
    [TestCase(1, true)]
    public async Task ExpirationUsesInjectedClockIncludingExactBoundary(int offsetMinutes, bool expectedSuccess)
    {
        using var db = new TestDatabase();
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var invitation = await db.Context.Invitations.SingleAsync(i => i.Code == "test");
        invitation.EndDate = now.AddMinutes(offsetMinutes);
        await db.Context.SaveChangesAsync();
        var clock = new Mock<INowDateTimeProvider>();
        clock.SetupGet(c => c.Now).Returns(now);
        var service = new InvitesService(db.Context, clock.Object);
        Assert.That((await service.ValidateAsync("test")).IsSuccess, Is.EqualTo(expectedSuccess));
        clock.VerifyGet(c => c.Now, Times.Once);
    }

    [Test]
    public async Task InactiveInvitationIsRejectedEvenWhenItExpiresInTheFuture()
    {
        using var db = new TestDatabase();
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var invitation = await db.Context.Invitations.SingleAsync(i => i.Code == "test");
        invitation.IsActive = false;
        invitation.EndDate = now.AddDays(1);
        await db.Context.SaveChangesAsync();
        var clock = new Mock<INowDateTimeProvider>();
        clock.SetupGet(c => c.Now).Returns(now);
        Assert.That((await new InvitesService(db.Context, clock.Object).ValidateAsync("test")).IsFailure, Is.True);
    }
}
