using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AutoFixture;
using Govor.Domain.Models.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Govor.Application.Tests.Services.Authentication;

[TestFixture]
public class JwtServiceTests
{
    private Fixture _fixture = null!;
    private Mock<IOptions<JwtAccessOption>> _jwtOptionsMock = null!;
    private Mock<IOptions<JwtRefreshOption>> _jwtRefreshOptionsMock = null!;
    private Mock<IInvitesService> _invitesServiceMock = null!;
    private IJwtService _jwtService = null!;
    private JwtAccessOption _testJwtAccessOptions = null!;
    private JwtRefreshOption _testJwtRefreshOptions = null!;
    [SetUp]
    public void SetUp()
    {
        _fixture = new Fixture();
        _fixture.Behaviors.OfType<ThrowingRecursionBehavior>().ToList().ForEach(b => _fixture.Behaviors.Remove(b));
        _fixture.Behaviors.Add(new OmitOnRecursionBehavior());

        _testJwtAccessOptions = new JwtAccessOption
        {
            SecretKey = "THIS_IS_A_TEST_SECRET_KEY_THAT_IS_LONG_ENOUGH_1234", // Ensure key size is sufficient for HMACSHA256
            Minutes = 5
        };

        _testJwtRefreshOptions = new JwtRefreshOption()
        {
            RefreshTokenLifetimeDays = 30
        };
        
        _jwtOptionsMock = new Mock<IOptions<JwtAccessOption>>();
        _jwtOptionsMock.Setup(o => o.Value).Returns(_testJwtAccessOptions);
        
        _jwtRefreshOptionsMock = new Mock<IOptions<JwtRefreshOption>>();
        _jwtRefreshOptionsMock.Setup(o => o.Value).Returns(_testJwtRefreshOptions);
        
        _invitesServiceMock = new Mock<IInvitesService>();
        
        _jwtService = new JwtService(
            _jwtOptionsMock.Object,
            _jwtRefreshOptionsMock.Object,
            _invitesServiceMock.Object);
    }

    [Test]
    public async Task GenerateJwtToken_ShouldReturnValidJwtString_WithCorrectClaims()
    {
        // Arrange
        var user = _fixture.Create<User>();
        var sessionId = Guid.NewGuid();
        var expectedRole = "User";
        _invitesServiceMock.Setup(s => s.GetRoleNameAsync(user)).ReturnsAsync(expectedRole);
    
        // Act
        var tokenString = await _jwtService.GenerateAccessTokenAsync(user, sessionId);
    
        // Assert
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        var claims = jwt.Claims.ToDictionary(c => c.Type, c => c.Value);

        Assert.That(claims["userId"], Is.EqualTo(user.Id.ToString()));
        Assert.That(claims["sid"], Is.EqualTo(sessionId.ToString()));
        Assert.That(claims[ClaimTypes.Role], Is.EqualTo(expectedRole));
        Assert.That(jwt.ValidTo, Is.GreaterThan(DateTime.UtcNow));
    }
    
    [Test]
    public async Task GenerateAccessTokenAsync_ShouldIncludeSessionIdAndRole()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid(), Username = "TestUser" };
        var sessionId = Guid.NewGuid();
        var role = "Admin";
        _invitesServiceMock.Setup(s => s.GetRoleNameAsync(user)).ReturnsAsync(role);

        // Act
        var token = await _jwtService.GenerateAccessTokenAsync(user, sessionId);
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        // Assert
        var sidClaim = jwt.Claims.FirstOrDefault(c => c.Type == "sid");
        var roleClaim = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role);

        Assert.That(sidClaim?.Value, Is.EqualTo(sessionId.ToString()));
        Assert.That(roleClaim?.Value, Is.EqualTo(role));
    }

    
    [Test]
    public async Task GenerateRefreshTokenAsync_ReturnsValidRefreshToken()
    {
        // Arrange
        var user = new User { Id = Guid.NewGuid() };

        // Act
        var token = await _jwtService.GenerateRefreshTokenAsync(user);

        // Assert
        Assert.That(token, Is.Not.Null);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        Assert.That(user.Id.ToString(), Is.EqualTo(jwt.Claims.First(c => c.Type == "userId").Value));
        Assert.That("refresh",Is.EqualTo(jwt.Claims.First(c => c.Type == "tokenType").Value));
    }
    
    [Test]
    public async Task GetPrincipalFromExpiredToken_ReturnsValidClaimsPrincipal()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_testJwtAccessOptions.SecretKey));

        var now = DateTime.UtcNow;

        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim("userId", userId.ToString())
            }),
            NotBefore = now.AddSeconds(-10),
            IssuedAt = now.AddSeconds(-10),
            Expires = now.AddSeconds(-5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        });

        var expiredToken = handler.WriteToken(token);

        // Act
        var principal = _jwtService.GetPrincipalFromExpiredToken(expiredToken);

        // Assert
        Assert.That(principal, Is.Not.Null);
        var claim = principal.FindFirst("userId");
        Assert.That(claim, Is.Not.Null);
        Assert.That(userId.ToString(), Is.EqualTo(claim.Value));
    }
}
