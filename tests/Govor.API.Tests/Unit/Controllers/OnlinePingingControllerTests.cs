using Govor.API.Controllers;
using Govor.Application.PingHandler;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class OnlinePingingControllerTests
{
    private Mock<ILogger<OnlinePingingController>> _loggerMock = null!;
    private Mock<IPingHandlerService> _pingHandlerServiceMock = null!;
    private Mock<ICurrentUserService> _currentUserServiceMock = null!;
    private OnlinePingingController _controller = null!;
    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<OnlinePingingController>>();
        _pingHandlerServiceMock = new Mock<IPingHandlerService>();
        _currentUserServiceMock = new Mock<ICurrentUserService>();
        _controller = new OnlinePingingController(_loggerMock.Object, _pingHandlerServiceMock.Object,
            _currentUserServiceMock.Object, Mock.Of<IUserPresenceReader>(), Mock.Of<IOnlineUserStore>());
    }

    [Test]
    public async Task Ping_ValidUserId_ReturnsOk()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _currentUserServiceMock.Setup(x => x.GetCurrentUserId()).Returns(userId);
        _pingHandlerServiceMock.Setup(x => x.Ping(userId)).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.Ping();

        // Assert
        Assert.That(result, Is.InstanceOf<OkResult>());
        _pingHandlerServiceMock.Verify(x => x.Ping(userId), Times.Once());
        _loggerMock.VerifyLog(LogLevel.Information, $"Ping from user {userId} processed successfully", Times.Once());
    }

    [Test]
    public async Task Ping_InvalidOperationException_Returns500()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var exception = new InvalidOperationException("User not found");
        _currentUserServiceMock.Setup(x => x.GetCurrentUserId()).Returns(userId);
        _pingHandlerServiceMock.Setup(x => x.Ping(userId)).ThrowsAsync(exception);

        // Act
        var result = await _controller.Ping();

        // Assert
        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(500));
        _loggerMock.VerifyLog(LogLevel.Error, exception.Message, Times.Once());
    }

    [Test]
    public async Task Ping_ServiceException_Returns500()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var exception = new UnauthorizedAccessException("Unauthorized");
        _currentUserServiceMock.Setup(x => x.GetCurrentUserId()).Returns(userId);
        _pingHandlerServiceMock.Setup(x => x.Ping(userId)).ThrowsAsync(exception);

        // Act
        var result = await _controller.Ping();

        // Assert
        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(500));
        _loggerMock.VerifyLog(LogLevel.Error, exception.Message, Times.Once());
    }

    [Test]
    public async Task Ping_GeneralException_ReturnsStatusCode500()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var exception = new Exception("Unexpected error");
        _currentUserServiceMock.Setup(x => x.GetCurrentUserId()).Returns(userId);
        _pingHandlerServiceMock.Setup(x => x.Ping(userId)).ThrowsAsync(exception);

        // Act
        var result = await _controller.Ping();

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
        _loggerMock.VerifyLog(LogLevel.Error, exception.Message, Times.Once());
    }

    [Test]
    public async Task Ping_CurrentUserException_Returns500WithoutPinging()
    {
        // Arrange
        var exception = new UnauthorizedAccessException("userID claim is missing or invalid");
        _currentUserServiceMock.Setup(x => x.GetCurrentUserId()).Throws(exception);

        // Act
        var result = await _controller.Ping();

        // Assert
        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(500));
        _loggerMock.VerifyLog(LogLevel.Error, exception.Message, Times.Once());
        _pingHandlerServiceMock.Verify(x => x.Ping(It.IsAny<Guid>()), Times.Never());
    }


    [TearDown]
    public void TearDown()
    {
        _controller?.Dispose();
    }
}

// Helper extension for verifying logger calls
public static class LoggerMockExtensions
{
    public static void VerifyLog<T>(this Mock<ILogger<T>> logger, LogLevel level, string message, Times times)
    {
        logger.Verify(
            x => x.Log(
                It.Is<LogLevel>(l => l == level),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(message)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);
    }
}
