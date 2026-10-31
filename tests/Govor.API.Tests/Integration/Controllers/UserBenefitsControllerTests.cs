using Govor.API.Controllers;
using Govor.API.Hubs;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.Profiles;
using Govor.Contracts.Requests;
using Govor.Domain.Models.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.API.Tests.Integration.Controllers;

[TestFixture]
public sealed class UserBenefitsControllerTests
{
    [Test]
    public async Task BroadcastFailureKeepsCommittedGrantSuccessfulAndReadable()
    {
        using var db = new TestDatabase();
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(x => x.SendCoreAsync("UserBenefitsUpdated", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("SignalR unavailable"));
        var hub = new Mock<IHubContext<ProfileHub>>();
        hub.Setup(x => x.Clients.All).Returns(proxy.Object);
        var benefits = new UserBenefitsService(db.Context);
        var controller = new UserBenefitsController(benefits, Mock.Of<ICurrentUserService>(), hub.Object,
            NullLogger<UserBenefitsController>.Instance);
        var result = await controller.SetPrivileges(db.Alice, new SetPrivilegesRequest(UserPrivileges.GovorPlus));
        Assert.That(result, Is.TypeOf<OkObjectResult>());
        Assert.That((await benefits.GetAsync(db.Alice)).IsGovorPlus, Is.True);
        proxy.Verify(x => x.SendCoreAsync("UserBenefitsUpdated", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
