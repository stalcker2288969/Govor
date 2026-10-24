using AutoMapper;
using Govor.API.Controllers;
using Govor.API.Hubs;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.Medias;
using Govor.Application.Profiles;
using Govor.Application.PushNotifications;
using Govor.Application.Users.UserSessions;
using Govor.Application.Users.UserOnlineStatus;
using Govor.Application.PingHandler;
using Govor.Contracts.Requests;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class ControllerFailureTests
{
    [TestCase(0,false)] [TestCase(0,true)] [TestCase(1,false)] [TestCase(1,true)] [TestCase(2,false)] [TestCase(2,true)]
    public async Task SessionRevocationExceptionsProduceExpectedStatus(int action,bool unauthorized)
    {
        var user=new Mock<ICurrentUserService>();var session=new Mock<ICurrentUserSessionService>();var revoker=new Mock<IUserSessionRevoker>();
        Exception error=unauthorized?new UnauthorizedAccessException():new Exception("private detail");
        revoker.Setup(x=>x.CloseSessionByIdAsync(It.IsAny<Guid>(),It.IsAny<Guid>())).ThrowsAsync(error);
        revoker.Setup(x=>x.CloseAllSessionsAsync(It.IsAny<Guid>())).ThrowsAsync(error);
        using var controller=new SessionController(NullLogger<SessionController>.Instance,user.Object,session.Object,Mock.Of<IUserSessionReader>(),revoker.Object,Mock.Of<IMapper>());
        var response=action switch{0=>await controller.CloseSession(Guid.NewGuid()),1=>await controller.CloseCurrent(),_=>await controller.CloseAllSessions()};
        if(unauthorized) Assert.That(response,Is.TypeOf<ForbidResult>());else {Assert.That(((ObjectResult)response).StatusCode,Is.EqualTo(500));Assert.That(((ObjectResult)response).Value,Is.Not.EqualTo("private detail"));}
    }
    [TestCase(false)] [TestCase(true)] public async Task ProfileByIdHandlesServiceExceptions(bool unauthorized)
    {
        var profiles=new Mock<IProfileService>();profiles.Setup(x=>x.GetUserProfileAsync(It.IsAny<Guid>())).ThrowsAsync(unauthorized?new UnauthorizedAccessException():new Exception("failure"));
        var controller=new ProfileController(NullLogger<ProfileController>.Instance,Mock.Of<IMediaService>(),profiles.Object,Mock.Of<ICurrentUserService>(),Mock.Of<IHubContext<ProfileHub>>(),Mock.Of<IMapper>());
        var response=await controller.DownloadProfileByUserId(Guid.NewGuid());
        if(unauthorized) Assert.That(response,Is.TypeOf<ForbidResult>());else Assert.That(((ObjectResult)response).StatusCode,Is.EqualTo(500));
    }
    [Test] public async Task ProfileByIdMapsRequestedUser()
    {
        var id=Guid.NewGuid();var value=new UserProfile{Id=id};var profiles=new Mock<IProfileService>();profiles.Setup(x=>x.GetUserProfileAsync(id)).ReturnsAsync(value);
        var mapper=new Mock<IMapper>();var dto=new Govor.Contracts.DTOs.UserProfileDto();mapper.Setup(x=>x.Map<Govor.Contracts.DTOs.UserProfileDto>(value)).Returns(dto);
        var controller=new ProfileController(NullLogger<ProfileController>.Instance,Mock.Of<IMediaService>(),profiles.Object,Mock.Of<ICurrentUserService>(),Mock.Of<IHubContext<ProfileHub>>(),mapper.Object);
        Assert.That(((OkObjectResult)await controller.DownloadProfileByUserId(id)).Value,Is.SameAs(dto));
    }
    [TestCase(0)] [TestCase(1)] [TestCase(2)] public async Task MediaUploadFailuresAreMapped(int kind)
    {
        var media=new Mock<IMediaService>();
        if(kind==0) media.Setup(x=>x.UploadMediaAsync(It.IsAny<Media>())).ReturnsAsync(Result.Failure<MediaUploadResult>(Error.ServerError("storage","failure")));
        else media.Setup(x=>x.UploadMediaAsync(It.IsAny<Media>())).ThrowsAsync(kind==1?new UnauthorizedAccessException():new Exception("failure"));
        using var stream=new MemoryStream([1]);using var controller=new MediaController(NullLogger<MediaController>.Instance,media.Object,Mock.Of<IAccesserToDownloadMedia>(),Mock.Of<ICurrentUserService>());
        var response=await controller.Upload(new(){FromFile=new FormFile(stream,0,1,"file","avatar.png"),OwnerType=MediaOwnerType.Avatar,MimeType="image/png"});
        if(kind==1) Assert.That(response,Is.TypeOf<ForbidResult>());else Assert.That(((ObjectResult)response).StatusCode,Is.EqualTo(500));
    }
    [TestCase(0)] [TestCase(1)] public async Task MediaUploadRejectsMissingMimeAndOversizeBeforeStorage(int kind)
    {
        var media=new Mock<IMediaService>(MockBehavior.Strict);var file=new Mock<IFormFile>();file.SetupGet(x=>x.Length).Returns(kind==0?1:20_000_001);
        using var controller=new MediaController(NullLogger<MediaController>.Instance,media.Object,Mock.Of<IAccesserToDownloadMedia>(),Mock.Of<ICurrentUserService>());
        Assert.That(await controller.Upload(new(){FromFile=file.Object,OwnerType=MediaOwnerType.Avatar,MimeType=kind==0?" ":"image/png"}),Is.TypeOf<BadRequestObjectResult>());media.VerifyNoOtherCalls();
    }
    [Test] public async Task OnlineStatusReaderFailureProduces500()
    {
        var presence=new Mock<IUserPresenceReader>();presence.Setup(x=>x.GetLastSeenAsync(It.IsAny<Guid>())).ThrowsAsync(new Exception("failure"));
        using var controller=new OnlinePingingController(NullLogger<OnlinePingingController>.Instance,Mock.Of<IPingHandlerService>(),Mock.Of<ICurrentUserService>(),presence.Object,Mock.Of<IOnlineUserStore>());
        Assert.That(((ObjectResult)await controller.GetStatus(Guid.NewGuid())).StatusCode,Is.EqualTo(500));
    }
    [Test] public async Task PushRegistrationHidesUnexpectedServiceFailure()
    {
        var push=new Mock<IPushTokenService>();push.Setup(x=>x.AddOrUpdateTokenAsync(It.IsAny<Guid>(),It.IsAny<Guid>(),"token","android")).ThrowsAsync(new Exception("private detail"));
        using var controller=new PushTokensController(push.Object,Mock.Of<ICurrentUserService>(),Mock.Of<ICurrentUserSessionService>(),NullLogger<PushTokensController>.Instance){ControllerContext=new(){HttpContext=new DefaultHttpContext()}};
        var result=(ObjectResult)await controller.RegisterToken(new(){Token="token",Platform="android"});
        Assert.That(result.StatusCode,Is.EqualTo(500));Assert.That(((ProblemDetails)result.Value!).Detail,Does.Not.Contain("private detail"));
    }
}

