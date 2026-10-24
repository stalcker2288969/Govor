using Govor.API.Controllers;
using Govor.Application.Friends;
using Govor.Application.Exceptions.VerifyFriendship;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.PrivateUserChats;
using Govor.Contracts.DTOs;
using Govor.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class PrivateChatControllerTests
{
    private readonly Guid _user=Guid.NewGuid(),_friend=Guid.NewGuid();
    private Mock<IVerifyFriendship> _verify=null!;
    private Mock<IUserPrivateChatsCreator> _creator=null!;
    private Mock<IUserPrivateChatsGetterService> _getter=null!;
    private PrivateChatController _controller=null!;
    [SetUp] public void Setup()
    {
        _verify=new(); _creator=new(); _getter=new();
        var user=new Mock<ICurrentUserService>(); user.Setup(x=>x.GetCurrentUserId()).Returns(_user);
        _controller=new(user.Object,_verify.Object,_creator.Object,_getter.Object,NullLogger<ChatLoadController>.Instance);
    }
    [TearDown] public void TearDown()=>_controller.Dispose();
    [Test] public async Task FriendshipIsVerifiedBeforeCreatingChat()
    {
        var order=new List<string>(); var chat=new PrivateChat{Id=Guid.NewGuid()};
        _verify.Setup(x=>x.VerifyAsync(_user,_friend)).Callback(()=>order.Add("verify")).Returns(Task.CompletedTask);
        _creator.Setup(x=>x.CreateAsync(_user,_friend)).Callback(()=>order.Add("create")).ReturnsAsync(chat);
        Assert.That(((OkObjectResult)await _controller.GetChatByFriendId(_friend)).Value,Is.EqualTo(chat.Id));
        Assert.That(order,Is.EqualTo(new[]{"verify","create"}));
    }
    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public async Task VerificationFailuresNeverCreateChat(int kind)
    {
        Exception error=kind switch{0=>new UnauthorizedAccessException(),1=>new ArgumentException("invalid"),2=>new FriendshipException("denied"),_=>new Exception("private")};
        _verify.Setup(x=>x.VerifyAsync(_user,_friend)).ThrowsAsync(error);
        var result=await _controller.GetChatByFriendId(_friend);
        if(kind is 0 or 2) Assert.That(result,Is.TypeOf<ForbidResult>());
        else Assert.That(((ObjectResult)result).StatusCode,Is.EqualTo(kind==1?400:500));
        _creator.VerifyNoOtherCalls();
    }
    [Test] public async Task ChatListSelectsOtherParticipantForBothOrientations()
    {
        _getter.Setup(x=>x.GetUserChatsAsync(_user)).ReturnsAsync([new(){Id=Guid.NewGuid(),UserAId=_user,UserBId=_friend},new(){Id=Guid.NewGuid(),UserAId=_friend,UserBId=_user}]);
        var result=(List<PrivateChatDto>)((OkObjectResult)await _controller.GetChatsByFriends()).Value!;
        Assert.That(result.Select(x=>x.FriendId),Is.EqualTo(new[]{_friend,_friend}));
    }
    [TestCase(false)] [TestCase(true)] public async Task ChatListHandlesErrors(bool unauthorized)
    {
        _getter.Setup(x=>x.GetUserChatsAsync(_user)).ThrowsAsync(unauthorized?new UnauthorizedAccessException():new Exception("private"));
        var result=await _controller.GetChatsByFriends();
        if(unauthorized) Assert.That(result,Is.TypeOf<ForbidResult>()); else Assert.That(((ObjectResult)result).StatusCode,Is.EqualTo(500));
    }
}
