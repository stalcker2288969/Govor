using AutoFixture;
using AutoMapper;
using Govor.API.Controllers.Friends;
using Govor.Contracts.DTOs;
using Govor.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class FriendsRequestQueryControllerTests
{
    private Fixture _fixture = null!;
    private Mock<ILogger<FriendsRequestQueryController>> _loggerMock = null!;
    private Mock<IFriendRequestQueryService> _friendsServiceMock = null!;
    private Mock<ICurrentUserService> _currentUserServiceMock = null!;
    private Mock<IMapper> _mapperMock = null!;
    private FriendsRequestQueryController _controller = null!;
    [SetUp]
    public void SetUp()
    {
        _fixture = new Fixture();
        _fixture.Behaviors.OfType<ThrowingRecursionBehavior>().ToList().ForEach(b => _fixture.Behaviors.Remove(b));
        _fixture.Behaviors.Add(new OmitOnRecursionBehavior());

        _loggerMock = new Mock<ILogger<FriendsRequestQueryController>>();
        _friendsServiceMock = new Mock<IFriendRequestQueryService>();
        _currentUserServiceMock = new Mock<ICurrentUserService>();
        _mapperMock = new Mock<IMapper>();
        
        _controller = new FriendsRequestQueryController(
            _loggerMock.Object,
            _friendsServiceMock.Object,
            _currentUserServiceMock.Object,
            _mapperMock.Object
        );
    }
    
    [Test]
    public async Task GetIncomingRequests_ValidRequest_ReturnsOkResult()
    {
        // Arrange
        var currentId = _fixture.Create<Guid>();
        var friendships = _fixture.CreateMany<Friendship>().ToList();
        var dtos = friendships.Select(f => new FriendshipDto()
        {
            AddresseeId =  f.AddresseeId,
            RequesterId =  f.RequesterId
        }).ToList();

        _currentUserServiceMock.Setup(c => c.GetCurrentUserId())
            .Returns(currentId);

        _friendsServiceMock.Setup(f => f.GetIncomingAsync(currentId))
            .ReturnsAsync(friendships);

        _mapperMock.Setup(f => f.Map<List<FriendshipDto>>(friendships)).Returns(dtos);
        
        // Act
        var result = await _controller.GetIncomingRequests();

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        List<FriendshipDto> value = okResult.Value as List<FriendshipDto>;
        
        Assert.That(value, Is.Not.Null);
        Assert.That(value.Count, Is.EqualTo(friendships.Count));
        Assert.That(value.Select(f => f.AddresseeId), Is.EquivalentTo(friendships.Select(f => f.AddresseeId)));
        Assert.That(value.Select(f => f.RequesterId), Is.EquivalentTo(friendships.Select(f => f.RequesterId)));
    }

    [Test]
    public async Task GetIncomingRequests_ServiceFailure_Returns500()
    {
        // Arrange
        var currentId = _fixture.Create<Guid>();

        _currentUserServiceMock.Setup(c => c.GetCurrentUserId())
            .Returns(currentId);

        _friendsServiceMock.Setup(f => f.GetIncomingAsync(currentId))
            .ThrowsAsync(new InvalidOperationException());

        // Act
        var result = await _controller.GetIncomingRequests();

        // Assert
        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task GetIncomingRequest_StatusCode500_IfThrowsSomeException()
    {
        // Arrange
        var currentId = _fixture.Create<Guid>();

        _currentUserServiceMock.Setup(c => c.GetCurrentUserId())
            .Returns(currentId);

        _friendsServiceMock.Setup(f => f.GetIncomingAsync(currentId))
            .ThrowsAsync(new Exception());

        // Act
        var result = await _controller.GetIncomingRequests();

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = result as ObjectResult;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }
    
    // Test for GetResponses action 
    [Test]
    public async Task GetResponses_ValidRequest_ReturnsOkResult()
    {
        // Arrange
        var currentId = _fixture.Create<Guid>();
        var friendships = _fixture.CreateMany<Friendship>().ToList();
        var dtos = friendships.Select(f => new FriendshipDto()
        {
            AddresseeId =  f.AddresseeId,
            RequesterId =  f.RequesterId
        }).ToList();

        _currentUserServiceMock.Setup(c => c.GetCurrentUserId())
            .Returns(currentId);

        _friendsServiceMock.Setup(f => f.GetResponsesAsync(currentId))
            .ReturnsAsync(friendships);

        _mapperMock.Setup(f => f.Map<List<FriendshipDto>>(friendships)).Returns(dtos);
        
        // Act
        var result = await _controller.GetResponses();

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = result as OkObjectResult;
        List<FriendshipDto> value = okResult.Value as List<FriendshipDto>;
        
        Assert.That(value, Is.Not.Null);
        Assert.That(value.Count, Is.EqualTo(friendships.Count));
        Assert.That(value.Select(f => f.AddresseeId), Is.EquivalentTo(friendships.Select(f => f.AddresseeId)));
        Assert.That(value.Select(f => f.RequesterId), Is.EquivalentTo(friendships.Select(f => f.RequesterId)));
    }

    [Test]
    public async Task GetResponses_ServiceFailure_Returns500()
    {
        // Arrange
        var currentId = _fixture.Create<Guid>();

        _currentUserServiceMock.Setup(c => c.GetCurrentUserId())
            .Returns(currentId);

        _friendsServiceMock.Setup(f => f.GetResponsesAsync(currentId))
            .ThrowsAsync(new InvalidOperationException());

        // Act
        var result = await _controller.GetResponses();

        // Assert
        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task GetResponses_StatusCode500_IfThrowsSomeException()
    {
        // Arrange
        var currentId = _fixture.Create<Guid>();

        _currentUserServiceMock.Setup(c => c.GetCurrentUserId())
            .Returns(currentId);

        _friendsServiceMock.Setup(f => f.GetResponsesAsync(currentId))
            .ThrowsAsync(new Exception());

        // Act
        var result = await _controller.GetResponses();

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = result as ObjectResult;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
    }

    [TearDown]
    public void TearDown()
    {
        _controller?.Dispose();
    }
}
