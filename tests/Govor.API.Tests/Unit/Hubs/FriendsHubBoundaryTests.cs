using AutoMapper;
using Govor.API.Common.SignalR.Helpers;
using Govor.API.Hubs;
using Govor.Application.Friends;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.API.Tests.Unit.Hubs;

[TestFixture]
public class FriendsHubBoundaryTests
{
    [TestCase(false,false)] [TestCase(false,true)] [TestCase(true,false)] [TestCase(true,true)]
    public async Task LifecycleHandlesMissingUserAndDisconnectErrors(bool invalid,bool error)
    {
        var id=invalid?Guid.Empty:Guid.NewGuid();var accessor=new Mock<IHubUserAccessor>();accessor.Setup(x=>x.GetUserId(It.IsAny<HubCallerContext>(),It.IsAny<bool>())).Returns(id);
        var context=new Mock<HubCallerContext>();context.SetupGet(x=>x.ConnectionId).Returns("connection");var groups=new Mock<IGroupManager>();
        using var hub=new FriendsHub(NullLogger<FriendsHub>.Instance,Mock.Of<IFriendRequestCommandService>(),accessor.Object,Mock.Of<IMapper>()){Context=context.Object,Groups=groups.Object};
        await hub.OnConnectedAsync();await hub.OnDisconnectedAsync(error?new Exception("disconnect"):null);
        context.Verify(x=>x.Abort(),invalid?Times.Once():Times.Never());
        groups.Verify(x=>x.AddToGroupAsync("connection",id.ToString(),default),invalid?Times.Never():Times.Once());
        groups.Verify(x=>x.RemoveFromGroupAsync("connection",id.ToString(),default),invalid?Times.Never():Times.Once());
    }
    [TestCase(0)] [TestCase(1)] [TestCase(2)] public async Task FailedFriendCommandsDoNotNotify(int operation)
    {
        var commands=new Mock<IFriendRequestCommandService>();var id=Guid.NewGuid();
        commands.Setup(x=>x.SendAsync(It.IsAny<Guid>(),id)).ReturnsAsync(Result.Failure<Friendship>(Error.Forbidden("denied","denied")));
        commands.Setup(x=>x.AcceptAsync(id,It.IsAny<Guid>())).ReturnsAsync(Result.Failure<Friendship>(Error.Forbidden("denied","denied")));
        commands.Setup(x=>x.RejectAsync(id,It.IsAny<Guid>())).ReturnsAsync(Result.Failure<Friendship>(Error.Forbidden("denied","denied")));
        var clients=new Mock<IHubCallerClients>(MockBehavior.Strict);
        using var hub=new FriendsHub(NullLogger<FriendsHub>.Instance,commands.Object,Mock.Of<IHubUserAccessor>(),Mock.Of<IMapper>()){Clients=clients.Object};
        var result=operation switch{0=>await hub.SendRequest(id),1=>await hub.AcceptRequest(id),_=>await hub.RejectRequest(id)};
        Assert.That(result.Status,Is.EqualTo(operation==0?HubResultStatus.ServerError:HubResultStatus.BadRequest));clients.VerifyNoOtherCalls();
    }
}

