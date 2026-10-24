using System.Security.Claims;
using Govor.Application.Infrastructure.Extensions;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Govor.Application.Tests.Infrastructure.Extensions;

[TestFixture]
[TestOf(typeof(CurrentUserSessionService))]
public class CurrentUserSessionServiceTests
{
    private Mock<IHttpContextAccessor> _httpContextAccessorMock = null!;
    private ICurrentUserSessionService _sessionService = null!;
    [SetUp]
    public void SetUp()
    {
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _sessionService = new CurrentUserSessionService(_httpContextAccessorMock.Object);
    }

    [Test]
    public void GetCurrentSessionId_ValidSidClaim_ReturnsGuid()
    {
        // Arrange
        var sid = Guid.NewGuid();
        var claims = new[] { new Claim("sid", sid.ToString()) };
        var identity = new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);

        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.Setup(x => x.User).Returns(principal);

        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContextMock.Object);

        // Act
        var result = _sessionService.GetUserSessionId();

        // Assert
        Assert.That(result, Is.EqualTo(sid));
    }
    
    [Test]
    public void GetCurrentSessionId_NoHttpContext_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns((HttpContext)null);

        // Act & Assert
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _sessionService.GetUserSessionId());
        Assert.That(ex.Message, Is.EqualTo("Session id (sid) claim is missing or invalid"));
    }
    
    
    [Test]
    public void GetCurrentSessionId_NoSidlaim_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var claims = new[] { new Claim("otherClaim", "value") };
        var identity = new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);

        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.Setup(x => x.User).Returns(principal);

        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContextMock.Object);

        // Act & Assert
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _sessionService.GetUserSessionId());
        Assert.That(ex.Message, Is.EqualTo("Session id (sid) claim is missing or invalid"));
    }
    
    [Test]
    public void GetUserSessionId_InvalidSidValueClaim_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var claims = new[] { new Claim("sid", "invalid-guid") };
        var identity = new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);

        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.Setup(x => x.User).Returns(principal);

        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContextMock.Object);

        // Act & Assert
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _sessionService.GetUserSessionId());
        Assert.That(ex.Message, Is.EqualTo("Session id (sid) claim is missing or invalid"));
    }

    [Test]
    public void GetUserSessionId_EmptySidValueClaim_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var claims = new[] { new Claim("sid", "") };
        var identity = new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);

        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.Setup(x => x.User).Returns(principal);

        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContextMock.Object);

        // Act & Assert
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _sessionService.GetUserSessionId());
        Assert.That(ex.Message, Is.EqualTo("Session id (sid) claim is missing or invalid"));
    }
}
