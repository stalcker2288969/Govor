using AutoMapper;
using Govor.API.Controllers;
using Govor.Application.Users.UserSessions;
using Govor.Contracts.DTOs;
using Govor.Domain.Common;
using Govor.Domain.Models.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class SessionControllerTests
{
    private SessionController _controller = null!;
    private Mock<IUserSessionReader> _reader = null!;
    private Mock<IUserSessionRevoker> _revoker = null!;
    private Mock<IMapper> _mapper = null!;
    private readonly Guid _user = Guid.NewGuid(), _session = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _reader = new Mock<IUserSessionReader>();
        _revoker = new Mock<IUserSessionRevoker>();
        _mapper = new Mock<IMapper>();
        var current = new Mock<ICurrentUserService>();
        current.Setup(c => c.GetCurrentUserId()).Returns(_user);
        var session = new Mock<ICurrentUserSessionService>();
        session.Setup(s => s.GetUserSessionId()).Returns(_session);
        _controller = new(NullLogger<SessionController>.Instance, current.Object,
            session.Object, _reader.Object, _revoker.Object, _mapper.Object);
    }

    [Test]
    public async Task SessionsAreReadForCurrentUserAndMapped()
    {
        var sessions = new List<UserSession> { new() { Id = _session, UserId = _user } };
        var dtos = new List<SessionDto> { new() { Id = _session } };
        _reader.Setup(r => r.GetAllSessionsAsync(_user)).ReturnsAsync(sessions);
        _mapper.Setup(m => m.Map<List<SessionDto>>(sessions)).Returns(dtos);
        Assert.That(((OkObjectResult)await _controller.GetAllSessions()).Value, Is.SameAs(dtos));
    }

    [Test]
    public async Task CloseActionsReturn204AndUseCurrentOwner()
    {
        _revoker.Setup(r => r.CloseSessionByIdAsync(_session, _user)).ReturnsAsync(Result.Success());
        _revoker.Setup(r => r.CloseAllSessionsAsync(_user)).ReturnsAsync(Result.Success());
        Assert.That(((StatusCodeResult)await _controller.CloseSession(_session)).StatusCode, Is.EqualTo(204));
        Assert.That(((StatusCodeResult)await _controller.CloseCurrent()).StatusCode, Is.EqualTo(204));
        Assert.That(((StatusCodeResult)await _controller.CloseAllSessions()).StatusCode, Is.EqualTo(204));
        _revoker.Verify(r => r.CloseSessionByIdAsync(_session, _user), Times.Exactly(2));
        _revoker.Verify(r => r.CloseAllSessionsAsync(_user), Times.Once);
    }

    [Test]
    public async Task EmptySessionIdDoesNotRevokeAnything()
    {
        Assert.That(await _controller.CloseSession(Guid.Empty), Is.TypeOf<BadRequestObjectResult>());
        _revoker.VerifyNoOtherCalls();
    }

    [TestCase(ErrorType.NotFound, 404)]
    [TestCase(ErrorType.Conflict, 409)]
    [TestCase(ErrorType.Forbidden, 403)]
    public async Task CloseFailuresReturnProblemDetails(ErrorType type, int status)
    {
        _revoker.Setup(r => r.CloseSessionByIdAsync(_session, _user))
            .ReturnsAsync(Result.Failure(new Error("Session.Failed", "failure", type)));
        foreach (var action in new Func<Task<IActionResult>>[] { () => _controller.CloseSession(_session), _controller.CloseCurrent })
        {
            var response = (ObjectResult)await action();
            Assert.That(response.StatusCode, Is.EqualTo(status));
            Assert.That(((ProblemDetails)response.Value!).Extensions["errorCode"], Is.EqualTo("Session.Failed"));
        }
    }

    [Test]
    public async Task UnauthorizedCurrentUserReturnsForbid()
    {
        _reader.Setup(r => r.GetAllSessionsAsync(_user)).ThrowsAsync(new UnauthorizedAccessException());
        Assert.That(await _controller.GetAllSessions(), Is.TypeOf<ForbidResult>());
    }

    [Test]
    public async Task UnexpectedReaderExceptionReturns500()
    {
        _reader.Setup(r => r.GetAllSessionsAsync(_user)).ThrowsAsync(new Exception("database unavailable"));
        Assert.That(((ObjectResult)await _controller.GetAllSessions()).StatusCode, Is.EqualTo(500));
    }
}
