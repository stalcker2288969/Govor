using Govor.API.Controllers.Authentication;
using Govor.API.Hubs.Infrastructure;
using Govor.Application.Authentication;
using Govor.Application.Users.UserSessions;
using Govor.Contracts.Requests;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class AuthControllerTests
{
    private Mock<IAccountService> _accounts = null!;
    private Mock<IInvitesService> _invites = null!;
    private Mock<IUserSessionOpener> _sessions = null!;
    private Mock<IChatNotificationService> _notifier = null!;
    private AuthController _controller = null!;
    private readonly User _user = new() { Id = Guid.NewGuid(), Username = "Иван" };
    private readonly Invitation _invite = new() { Id = Guid.NewGuid(), Code = "test" };
    private readonly RefreshResult _tokens = new("refresh", "access");

    [SetUp]
    public void SetUp()
    {
        _accounts = new Mock<IAccountService>();
        _invites = new Mock<IInvitesService>();
        _sessions = new Mock<IUserSessionOpener>();
        _notifier = new Mock<IChatNotificationService>();
        _controller = new(_accounts.Object, _invites.Object, _sessions.Object,
            NullLogger<AuthController>.Instance, _notifier.Object);
        _invites.Setup(i => i.ValidateAsync("test")).ReturnsAsync(_invite);
        _accounts.Setup(a => a.RegistrationAsync("Иван", "secret", _invite)).ReturnsAsync(_user);
        _accounts.Setup(a => a.LoginAsync("Иван", "secret")).ReturnsAsync(_user);
        _sessions.Setup(s => s.OpenSessionAsync(_user, "phone")).ReturnsAsync(_tokens);
    }

    [Test]
    public async Task RegistrationValidatesInvitationCreatesAccountNotifiesGroupsThenOpensSession()
    {
        var order = new List<string>();
        _notifier.Setup(n => n.NotifyUserJoinedGroupsAsync(_user.Id)).Callback(() => order.Add("notify")).Returns(Task.CompletedTask);
        _sessions.Setup(s => s.OpenSessionAsync(_user, "phone")).Callback(() => order.Add("session")).ReturnsAsync(_tokens);
        var response = await _controller.Register(new RegistrationRequest { Name = "Иван", Password = "secret", InviteLink = "test", DeviceInfo = "phone" });
        Assert.That(((OkObjectResult)response).Value, Is.EqualTo(_tokens));
        Assert.That(order, Is.EqualTo(new[] { "notify", "session" }));
        _invites.Verify(i => i.ValidateAsync("test"), Times.Once);
        _accounts.Verify(a => a.RegistrationAsync("Иван", "secret", _invite), Times.Once);
    }

    [Test]
    public async Task InvalidInvitationStopsRegistrationPipeline()
    {
        _invites.Setup(i => i.ValidateAsync("test")).ReturnsAsync(Result.Failure<Invitation>(Error.Validation("Invite.Invalid", "invalid")));
        var response = (ObjectResult)await _controller.Register(new RegistrationRequest { Name = "Иван", Password = "secret", InviteLink = "test" });
        Assert.That(response.StatusCode, Is.EqualTo(400));
        Assert.That(((ProblemDetails)response.Value!).Extensions["errorCode"], Is.EqualTo("Invite.Invalid"));
        _accounts.VerifyNoOtherCalls();
        _sessions.VerifyNoOtherCalls();
        _notifier.VerifyNoOtherCalls();
    }

    [TestCase(ErrorType.Conflict, 409)]
    [TestCase(ErrorType.Validation, 400)]
    public async Task RegistrationFailureDoesNotNotifyOrOpenSession(ErrorType type, int status)
    {
        _accounts.Setup(a => a.RegistrationAsync("Иван", "secret", _invite))
            .ReturnsAsync(Result.Failure<User>(new Error("Auth.Failed", "failure", type)));
        var response = (ObjectResult)await _controller.Register(new RegistrationRequest { Name = "Иван", Password = "secret", InviteLink = "test" });
        Assert.That(response.StatusCode, Is.EqualTo(status));
        _sessions.VerifyNoOtherCalls();
        _notifier.VerifyNoOtherCalls();
    }

    [Test]
    public async Task LoginReturnsTokensForTheRequestedDevice()
    {
        var response = (OkObjectResult)await _controller.Login(new LoginRequest { Name = "Иван", Password = "secret", DeviceInfo = "phone" });
        Assert.That(response.Value, Is.EqualTo(_tokens));
        _sessions.Verify(s => s.OpenSessionAsync(_user, "phone"), Times.Once);
        _invites.VerifyNoOtherCalls();
        _notifier.VerifyNoOtherCalls();
    }

    [TestCase(ErrorType.NotFound, 404)]
    [TestCase(ErrorType.Failure, 400)]
    [TestCase(ErrorType.Unauthorized, 401)]
    public async Task FailedLoginDoesNotOpenSession(ErrorType type, int status)
    {
        _accounts.Setup(a => a.LoginAsync("Иван", "secret")).ReturnsAsync(Result.Failure<User>(new Error("Auth.Failed", "failure", type)));
        var response = (ObjectResult)await _controller.Login(new LoginRequest { Name = "Иван", Password = "secret", DeviceInfo = "phone" });
        Assert.That(response.StatusCode, Is.EqualTo(status));
        _sessions.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SessionFailureIsReturnedToClient()
    {
        _sessions.Setup(s => s.OpenSessionAsync(_user, "phone"))
            .ReturnsAsync(Result.Failure<RefreshResult>(Error.ServerError("Session.Failed", "failure")));
        Assert.That(((ObjectResult)await _controller.Login(new LoginRequest { Name = "Иван", Password = "secret", DeviceInfo = "phone" })).StatusCode, Is.EqualTo(500));
    }
}

[TestFixture]
public class RefreshControllerTests
{
    [Test]
    public async Task SuccessfulRefreshReturnsCurrentTokenContract()
    {
        var service = new Mock<IUserSessionRefresher>();
        var tokens = new RefreshResult("new-refresh", "new-access");
        service.Setup(s => s.RefreshTokenAsync("old")).ReturnsAsync(tokens);
        var controller = new RefreshController(NullLogger<RefreshController>.Instance, service.Object);
        var response = (OkObjectResult)await controller.Refresh(new RefreshTokenRequest { RefreshToken = "old" });
        Assert.That(response.Value, Is.SameAs(tokens));
    }

    [TestCase(ErrorType.Failure, 400)]
    [TestCase(ErrorType.Unauthorized, 401)]
    [TestCase(ErrorType.ServerError, 500)]
    public async Task RefreshFailureMapsToProblemDetails(ErrorType type, int status)
    {
        var service = new Mock<IUserSessionRefresher>();
        service.Setup(s => s.RefreshTokenAsync("invalid"))
            .ReturnsAsync(Result.Failure<RefreshResult>(new Error("Auth.InvalidToken", "invalid", type)));
        var controller = new RefreshController(NullLogger<RefreshController>.Instance, service.Object);
        var response = (ObjectResult)await controller.Refresh(new RefreshTokenRequest { RefreshToken = "invalid" });
        Assert.That(response.StatusCode, Is.EqualTo(status));
        Assert.That(((ProblemDetails)response.Value!).Extensions["errorCode"], Is.EqualTo("Auth.InvalidToken"));
    }
}
