using AutoFixture;
using Govor.Domain.Models;
using Govor.Domain.Models.Users;
using Microsoft.Extensions.Logging;
using Moq;

namespace Govor.Application.Tests.Services.UserOnlineStatus;

[TestFixture]
[TestOf(typeof(UserNotificationScopeService))]
public class UserNotificationScopeServiceTests
{
    private Mock<ILogger<UserNotificationScopeService>> _mockLogger = null!;
    private Mock<IFriendshipService> _mockFriendshipService = null!;
    private IUserNotificationScopeService _service = null!;
    private Fixture _fixture = null!;
    private Guid _userId;
    [SetUp]
    public void SetUp()
    {
        _fixture = new Fixture();
        _fixture.Behaviors.OfType<ThrowingRecursionBehavior>().ToList().ForEach(b => _fixture.Behaviors.Remove(b));
        _fixture.Behaviors.Add(new OmitOnRecursionBehavior());
        
        _userId = Guid.NewGuid();
        
        _mockLogger = new Mock<ILogger<UserNotificationScopeService>>();
        _mockFriendshipService = new Mock<IFriendshipService>();
        
        _service = new UserNotificationScopeService(_mockLogger.Object, _mockFriendshipService.Object);
    }
    
    [Test]
    public async Task GetNotifiedUsers_ReturnsNotifiedUsers()
    {
        // Arrange 
        var random = new Random();
        var users = _fixture.CreateMany<User>(random.Next(3, 10)).ToList();
        
        _mockFriendshipService.Setup(f => f.GetFriendsAsync(_userId))
            .ReturnsAsync(users);
        
        // Act 
        var result = await _service.GetNotifiedUsers(_userId);
        // Assert 
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(users.Count));
        Assert.That(result, Is.EquivalentTo(users.Select(u => u.Id)));
    }

    [Test]
    public async Task GetNotifiedUsers_PropagatesFriendshipServiceFailure()
    {
        // Arrange 
        _mockFriendshipService.Setup(f => f.GetFriendsAsync(_userId))
            .ThrowsAsync(new InvalidOperationException("Database is empty."));

        // Act & Assert 
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await _service.GetNotifiedUsers(_userId));
        Assert.That(ex.Message, Is.EqualTo("Database is empty."));
    }
}
