using AutoFixture;
using AutoMapper;
using Govor.API.Controllers.Friends;
using Govor.Application.Exceptions.FriendsService;
using Govor.Contracts.DTOs;
using Govor.Domain.Models.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class FriendshipControllerTests
{
    private Fixture _fixture = null!;
    private Mock<ILogger<FriendshipController>> _loggerMock = null!;
    private Mock<IFriendshipService> _friendsServiceMock = null!;
    private Mock<ICurrentUserService> _currentUserServiceMock = null!;
    private Mock<IMapper> _mapperMock = null!;
    private FriendshipController _controller = null!;
    [SetUp]
    public void SetUp()
    {
        _fixture = new Fixture();
        _fixture.Behaviors.OfType<ThrowingRecursionBehavior>().ToList().ForEach(b => _fixture.Behaviors.Remove(b));
        _fixture.Behaviors.Add(new OmitOnRecursionBehavior());

        _loggerMock = new Mock<ILogger<FriendshipController>>();
        _friendsServiceMock = new Mock<IFriendshipService>();
        _currentUserServiceMock = new Mock<ICurrentUserService>();
        _mapperMock = new Mock<IMapper>();
        
        _controller = new FriendshipController(
            _loggerMock.Object,
            _friendsServiceMock.Object,
            _currentUserServiceMock.Object,
            _mapperMock.Object
        );
    }
    
    // Tests for Search action
    [Test]
    public async Task Search_ValidRequest_ReturnsOkResult()
    {
        var users = _fixture.CreateMany<User>().ToList();
        var dtos = users.Select(u => new UserDto { Id = u.Id }).ToList();
        var userId = _fixture.Create<Guid>();
        var query = _fixture.Create<string>();

        _currentUserServiceMock.Setup(c => c.GetCurrentUserId()).Returns(userId);

        _friendsServiceMock.Setup(f => f.SearchUsersAsync(query, userId))
            .ReturnsAsync(users);
        
        _mapperMock.Setup(m => m.Map<List<UserDto>>(users)).Returns(dtos);
        
        // Act
        var result = await _controller.Search(query);

        var okResult = result as OkObjectResult;
        dynamic value = okResult.Value;

        List<UserDto> userDtos = value as List<UserDto>;

        // Assert
        Assert.That(value, Is.Not.Null);
        Assert.That(value.Count, Is.EqualTo(users.Count));
        Assert.That(userDtos.Select(u => u.Id), Is.EqualTo(users.Select(u => u.Id)));
    }

    [Test]
    public async Task Search_InvalidQuery_BadRequest()
    {
        // Act
        var result = await _controller.Search(string.Empty);
        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequestResult = result as BadRequestObjectResult;
        Assert.That(badRequestResult.Value, Is.EqualTo("Query cannot be empty"));
    }
    
    [Test]
    public async Task Search_StatusCode500_IfThrowsSomeException()
    {
        // Arrange
        _friendsServiceMock.Setup(f => f.SearchUsersAsync(It.IsAny<string>(), It.IsAny<Guid>()))
            .ThrowsAsync(new Exception(_fixture.Create<string>()));

        // Act
        var result = await _controller.Search(_fixture.Create<string>());
        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = result as ObjectResult;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }
    
    [Test]
    public async Task GetFriends_ValidRequest_ReturnsOkResult()
    {
        // Arrange
        var currentId = _fixture.Create<Guid>();
        var users = _fixture.CreateMany<User>().ToList();
        var dtos = users.Select(u => new UserDto { Id = u.Id }).ToList();
        
        _currentUserServiceMock.Setup(c => c.GetCurrentUserId())
            .Returns(currentId);

        _friendsServiceMock.Setup(f => f.GetFriendsAsync(currentId))
            .ReturnsAsync(users);
        
        _mapperMock.Setup(m => m.Map<List<UserDto>>(users)).Returns(dtos);
        
        // Act
        var result = await _controller.GetFriends();

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        List<UserDto> value = okResult.Value as List<UserDto>;
        Assert.That(value, Is.Not.Null);
        Assert.That(value.Count, Is.EqualTo(users.Count));
        Assert.That(value.Select(u => u.Id), Is.EqualTo(users.Select(u => u.Id)));
    }

    [Test]
    public async Task GetFriends_ServiceFailure_Returns500()
    {
        // Arrange
        var currentId = _fixture.Create<Guid>();
        var users = _fixture.CreateMany<User>().ToList();

        _currentUserServiceMock.Setup(c => c.GetCurrentUserId())
            .Returns(currentId);

        _friendsServiceMock.Setup(f => f.GetFriendsAsync(currentId))
            .ThrowsAsync(new InvalidOperationException());

        // Act
        var result = await _controller.GetFriends();

        // Assert
        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task GetFriends_Exception_ReturnsStatusCode500()
    {
        // Arrange
        var currentId = _fixture.Create<Guid>();
        var users = _fixture.CreateMany<User>().ToList();

        _currentUserServiceMock.Setup(c => c.GetCurrentUserId())
            .Returns(currentId);

        _friendsServiceMock.Setup(f => f.GetFriendsAsync(currentId))
            .ThrowsAsync(new Exception());

        // Act
        var result = await _controller.GetFriends();

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = result as ObjectResult;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }
    
    
    [TearDown]
    public void TearDown()
    {
        _controller.Dispose();
    }
}
