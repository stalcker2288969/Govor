using System.Security.Claims;
using Govor.API.Common.SignalR.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace Govor.API.Tests.Common.SignalR.Helpers;

[TestFixture]
[TestOf(typeof(HubUserAccessor))]
public class HubUserAccessorTests
{
    private HubUserAccessor _accessor = null!;
    private Mock<ILogger<HubUserAccessor>> _loggerMock = null!;
    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<HubUserAccessor>>();
        _accessor = new HubUserAccessor(_loggerMock.Object);
    }

    private HubCallerContext CreateContextWithClaims(params Claim[] claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims));
        var contextMock = new Mock<HubCallerContext>();
        contextMock.Setup(c => c.User).Returns(principal);
        return contextMock.Object;
    }

    [Test]
    public void GetUserId_ValidClaim_ReturnsGuid()
    {
        // Arrange 
        var expectedGuid = Guid.NewGuid();
        var context = CreateContextWithClaims(new Claim("userId", expectedGuid.ToString()));

        // Act 
        var result = _accessor.GetUserId(context);

        // Assert 
        Assert.That(expectedGuid, Is.EqualTo(result));
    }

    [Test]
    public void GetUserId_InvalidClaim_ThrowsException_WhenNotSuppressed()
    {
        // Arrange 
        var context = CreateContextWithClaims(new Claim("userId", "not-a-guid"));

        // Act & Assert 
        Assert.Throws<UnauthorizedAccessException>(() => _accessor.GetUserId(context, suppressException: false));
    }

    [Test]
    public void GetUserId_InvalidClaim_ReturnsEmptyGuid_WhenSuppressed()
    {
        // Arrange 
        var context = CreateContextWithClaims(new Claim("userId", "not-a-guid"));

        // Act
        var result = _accessor.GetUserId(context, suppressException: true);

        // Assert 
        Assert.That(Guid.Empty, Is.EqualTo(result));
    }

    [Test]
    public void GetUserId_NoClaim_ThrowsException_WhenNotSuppressed()
    {
        // Arrange 
        var context = CreateContextWithClaims();
        
        // Act & Assert 
        Assert.Throws<UnauthorizedAccessException>(() => _accessor.GetUserId(context, suppressException: false));
    }

    [Test]
    public void GetUserId_NoClaim_ReturnsEmptyGuid_WhenSuppressed()
    {
        // Arrange 
        var context = CreateContextWithClaims();
        
        // Act 
        var result = _accessor.GetUserId(context, suppressException: true);
        
        // Assert 
        Assert.That(Guid.Empty,  Is.EqualTo(result));
    }

    [Test]
    public void GetUserId_UserIsNull_ReturnsEmptyGuid_WhenSuppressed()
    {
        // Arrange 
        var contextMock = new Mock<HubCallerContext>();
        contextMock.Setup(c => c.User).Returns((ClaimsPrincipal?)null);
        
        // Act
        var result = _accessor.GetUserId(contextMock.Object, suppressException: true);
        
        // Assert 
        Assert.That(Guid.Empty, Is.EqualTo(result));
    }

    [Test]
    public void GetUserId_UserIsNull_ThrowsException_WhenNotSuppressed()
    {
        // Arrange 
        var contextMock = new Mock<HubCallerContext>();
        contextMock.Setup(c => c.User).Returns((ClaimsPrincipal?)null);
        // Act & Assert 
        Assert.Throws<UnauthorizedAccessException>(() => _accessor.GetUserId(contextMock.Object, suppressException: false));
    }
}
