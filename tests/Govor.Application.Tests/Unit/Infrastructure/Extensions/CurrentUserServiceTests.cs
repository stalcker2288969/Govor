using System.Security.Claims;
using Govor.Application.Infrastructure.Extensions;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Govor.Application.Tests.Infrastructure.Extensions;

[TestFixture]
public class CurrentUserServiceTests
{
    private Mock<IHttpContextAccessor> _httpContextAccessorMock = null!;
    private ICurrentUserService _currentUserService = null!;
    [SetUp]
    public void SetUp()
    {
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _currentUserService = new CurrentUserService(_httpContextAccessorMock.Object);
    }

    [Test]
    public void GetCurrentUserId_ValidUserIdClaim_ReturnsGuid()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var claims = new[] { new Claim("userId", userId.ToString()) };
        var identity = new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);

        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.Setup(x => x.User).Returns(principal);

        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContextMock.Object);

        // Act
        var result = _currentUserService.GetCurrentUserId();

        // Assert
        Assert.That(result, Is.EqualTo(userId));
    }

    [Test]
    public void GetCurrentUserId_NoHttpContext_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns((HttpContext)null);

        // Act & Assert
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _currentUserService.GetCurrentUserId());
        Assert.That(ex.Message, Is.EqualTo("userID claim is missing or invalid"));
    }

    [Test]
    public void GetCurrentUserId_NoUserIdClaim_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var claims = new[] { new Claim("otherClaim", "value") };
        var identity = new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);

        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.Setup(x => x.User).Returns(principal);

        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContextMock.Object);

        // Act & Assert
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _currentUserService.GetCurrentUserId());
        Assert.That(ex.Message, Is.EqualTo("userID claim is missing or invalid"));
    }

    [Test]
    public void GetCurrentUserId_InvalidUserIdClaim_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var claims = new[] { new Claim("userId", "invalid-guid") };
        var identity = new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);

        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.Setup(x => x.User).Returns(principal);

        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContextMock.Object);

        // Act & Assert
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _currentUserService.GetCurrentUserId());
        Assert.That(ex.Message, Is.EqualTo("userID claim is missing or invalid"));
    }

    [Test]
    public void GetCurrentUserId_EmptyUserIdClaim_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var claims = new[] { new Claim("userId", "") };
        var identity = new ClaimsIdentity(claims);
        var principal = new ClaimsPrincipal(identity);

        var httpContextMock = new Mock<HttpContext>();
        httpContextMock.Setup(x => x.User).Returns(principal);

        _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(httpContextMock.Object);

        // Act & Assert
        var ex = Assert.Throws<UnauthorizedAccessException>(() => _currentUserService.GetCurrentUserId());
        Assert.That(ex.Message, Is.EqualTo("userID claim is missing or invalid"));
    }
}
