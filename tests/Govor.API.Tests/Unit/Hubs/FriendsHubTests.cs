using AutoFixture;
using AutoMapper;
using Govor.API.Common.SignalR.Helpers;
using Govor.API.Hubs;
using Govor.Application.Exceptions.FriendsService;
using Govor.Contracts.DTOs;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace Govor.API.Tests.Unit.Hubs;

[TestFixture]
public class FriendsHubTests
{
    private Mock<IFriendRequestCommandService> _friendRequestServiceMock = null!;
    private Mock<IHubUserAccessor> _currentUserServiceMock = null!;
    private Mock<IHubCallerClients> _clientsMock = null!;
    private Mock<IClientProxy> _clientProxyMock = null!;
    private Mock<ILogger<FriendsHub>> _loggerMock = null!;
    private Mock<IMapper> _mapperMock = null!;
    private FriendsHub _hub = null!;
    private Fixture _fixture = null!;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _targetUserId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _fixture = new Fixture();
        _fixture.Behaviors.OfType<ThrowingRecursionBehavior>().ToList().ForEach(b => _fixture.Behaviors.Remove(b));
        _fixture.Behaviors.Add(new OmitOnRecursionBehavior());
        
        _friendRequestServiceMock = new Mock<IFriendRequestCommandService>();
        _currentUserServiceMock = new Mock<IHubUserAccessor>();
        _clientsMock = new Mock<IHubCallerClients>();
        _clientProxyMock = new Mock<IClientProxy>();
        _loggerMock = new Mock<ILogger<FriendsHub>>();
        _mapperMock = new Mock<IMapper>();
        
        _currentUserServiceMock.Setup(x => x.GetUserId(
                It.IsAny<HubCallerContext>(),
            It.IsAny<bool>()))
            .Returns(_userId);
        
        _clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxyMock.Object);
        
        _hub = new FriendsHub(
            _loggerMock.Object,
            _friendRequestServiceMock.Object,
            _currentUserServiceMock.Object,
           _mapperMock.Object)
        {
            Clients = _clientsMock.Object
        };
    }
    
    // Tests for SendRequest action 
    [Test]
    public async Task SendRequest_ShouldReturnCreated_WhenSuccess()
    {
        var friendship = _fixture.Build<Friendship>()
            .With(f => f.Status, FriendshipStatus.Pending)
            .With(f => f.RequesterId, _userId)
            .With(f =>f.AddresseeId, _targetUserId)
            .Create();
        
        var dto = new FriendshipDto()
        {
            Id = friendship.Id,
            Status = FriendshipStatus.Accepted,
            AddresseeId = friendship.AddresseeId,
            RequesterId = friendship.RequesterId,
        };
        
        _mapperMock.Setup(f => f.Map<FriendshipDto>(friendship))
            .Returns(dto);
        
        _friendRequestServiceMock.Setup(f => f.SendAsync(_userId, _targetUserId))
            .ReturnsAsync(friendship);
        
        // Act
        var result = await _hub.SendRequest(_targetUserId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.Created));
        
        _friendRequestServiceMock.Verify(s => s.SendAsync(_userId, _targetUserId), Times.Once);
        
        _clientProxyMock.Verify(c => c.SendCoreAsync(
            "FriendRequestReceived",
            It.Is<object[]>(o => o[0]!.Equals(dto)),
            default), Times.Once);
    }
    
    [Test]
    public async Task SendRequest_ShouldReturnBadRequest_WhenInvalidOperation()
    {
        // Arrange 
        _friendRequestServiceMock
            .Setup(s => s.SendAsync(_userId, _targetUserId))
            .ThrowsAsync(new InvalidOperationException("Invalid"));

        // Act 
        var result = await _hub.SendRequest(_targetUserId);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.BadRequest));
        Assert.That(result.ErrorMessage, Is.EqualTo("Invalid"));
    }
    
    [Test]
    public async Task SendRequest_ShouldReturnConflict_WhenAlreadySent()
    {
        // Arrange 
        _friendRequestServiceMock
            .Setup(s => s.SendAsync(_userId, _targetUserId))
            .ThrowsAsync(new RequestAlreadySentException(_userId, _targetUserId));
    
        // Act 
        var result = await _hub.SendRequest(_targetUserId);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.Conflict));
        Assert.That(result.ErrorMessage, Is.EqualTo($"Request was already sent from {_userId} to {_targetUserId}"));
    }
    
    [Test]
    public async Task SendRequest_ShouldReturnUnauthorized_WhenCurrentUserIsNotAuthenticated()
    {
        // Arrange 
        _currentUserServiceMock.Setup(x => x.GetUserId(It.IsAny<HubCallerContext>(), It.IsAny<bool>()))
            .Throws(new UnauthorizedAccessException("userId claim is missing or invalid"));

        // Act 
        var result = await _hub.SendRequest(_targetUserId);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.Unauthorized));
        Assert.That(result.ErrorMessage, Is.EqualTo("userId claim is missing or invalid"));
    }
    
    [Test]
    public async Task SendRequest_ShouldReturnError_WhenSomeExceptionOccurs()
    {
        // Arrange 
        _friendRequestServiceMock
            .Setup(s => s.SendAsync(_userId, _targetUserId))
            .ThrowsAsync(new Exception("error"));
    
        // Act 
        var result = await _hub.SendRequest(_targetUserId);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.ServerError));
        Assert.That(result.ErrorMessage, Is.EqualTo("Unexpected error! Please try later!"));
    }
    
    // Test for AcceptRequest action
    [Test]
    public async Task AcceptRequest_ShouldReturnOk_WhenSuccess()
    {
        // Arrange 
        var friendship = _fixture.Build<Friendship>()
            .With(f => f.Status, FriendshipStatus.Pending)
            .Create();
        var dto = new FriendshipDto()
        {
            Id = friendship.Id,
            Status = FriendshipStatus.Accepted,
            AddresseeId = friendship.AddresseeId,
            RequesterId = friendship.RequesterId
        };
        
        _mapperMock.Setup(f => f.Map<FriendshipDto>(friendship))
            .Returns(dto);
        
        _friendRequestServiceMock.Setup(f => f.AcceptAsync(friendship.Id, _userId))
            .ReturnsAsync(friendship);
        
        // Act 
        var result = await _hub.AcceptRequest(friendship.Id);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.Success));
        _friendRequestServiceMock.Verify(s => s.AcceptAsync(friendship.Id, _userId), Times.Once);
        
        _clientProxyMock.Verify(c => c.SendCoreAsync(
            "FriendRequestAccepted",
            It.Is<object[]>(o => o.Length == 1 && o[0] == dto),
            default), Times.Once);

    }
    
    [Test]
    public async Task AcceptRequest_ShouldReturnBadRequest_InvalidOperation()
    {
        // Arrange 
        var friendshipId = Guid.NewGuid();
        _friendRequestServiceMock
            .Setup(s => s.AcceptAsync(friendshipId, _userId))
            .ThrowsAsync(new InvalidOperationException("Invalid"));

        // Act 
        var result = await _hub.AcceptRequest(friendshipId);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.BadRequest));
        Assert.That(result.ErrorMessage, Is.EqualTo("Invalid"));
    }
    
        
    [Test]
    public async Task AcceptRequest_ShouldReturnUnauthorized_UnauthorizedAccess()
    {
        // Arrange 
        var friendshipId = Guid.NewGuid();
        _friendRequestServiceMock
            .Setup(s => s.AcceptAsync(friendshipId, _userId))
            .ThrowsAsync(new UnauthorizedAccessException("userId claim is missing or invalid"));

        // Act 
        var result = await _hub.AcceptRequest(friendshipId);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.Unauthorized));
        Assert.That(result.ErrorMessage, Is.EqualTo("userId claim is missing or invalid"));
    }
    
    [Test]
    public async Task AcceptRequest_ShouldReturnError_WhenSomeExceptionOccurs()
    {
        // Arrange 
        var friendshipId = Guid.NewGuid();
        _friendRequestServiceMock
            .Setup(s => s.AcceptAsync(friendshipId, _userId))
            .ThrowsAsync(new Exception("error"));

        // Act 
        var result = await _hub.AcceptRequest(friendshipId);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.ServerError));
        Assert.That(result.ErrorMessage, Is.EqualTo("Unexpected error! Please try later!"));
    }
    
    // Test for RejectRequest action 
    [Test]
    public async Task RejectRequest_ShouldReturnOk_WhenSuccess()
    {
        // Arrange 
        var friendship = _fixture.Build<Friendship>()
            .With(f => f.Status, FriendshipStatus.Pending)
            .Create();
        var dto = new FriendshipDto()
        {
            Id = friendship.Id,
            Status = FriendshipStatus.Accepted,
            AddresseeId = friendship.AddresseeId,
            RequesterId = friendship.RequesterId
        };
        
        _mapperMock.Setup(f => f.Map<FriendshipDto>(friendship))
            .Returns(dto);
        
        _friendRequestServiceMock.Setup(f => f.RejectAsync(friendship.Id, _userId))
            .ReturnsAsync(friendship);
        
        // Act 
        var result = await _hub.RejectRequest(friendship.Id);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.Success));
        _friendRequestServiceMock.Verify(s => s.RejectAsync(friendship.Id, _userId), Times.Once);
        
        _clientProxyMock.Verify(c => c.SendCoreAsync(
            "FriendRequestRejected",
            It.Is<object[]>(o => o[0]!.Equals(dto)),
            default), Times.Once);
    }
    
    [Test]
    public async Task RejectRequest_ShouldReturnBadRequest_InvalidOperation()
    {
        // Arrange 
        var friendshipId = Guid.NewGuid();
        _friendRequestServiceMock
            .Setup(s => s.RejectAsync(friendshipId, _userId))
            .ThrowsAsync(new InvalidOperationException("Invalid"));

        // Act 
        var result = await _hub.RejectRequest(friendshipId);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.BadRequest));
        Assert.That(result.ErrorMessage, Is.EqualTo("Invalid"));
    }
    
        
    [Test]
    public async Task RejectRequest_ShouldReturnUnauthorized_UnauthorizedAccess()
    {
        // Arrange 
        var friendshipId = Guid.NewGuid();
        _friendRequestServiceMock
            .Setup(s => s.RejectAsync(friendshipId, _userId))
            .ThrowsAsync(new UnauthorizedAccessException("userId claim is missing or invalid"));

        // Act 
        var result = await _hub.RejectRequest(friendshipId);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.Unauthorized));
        Assert.That(result.ErrorMessage, Is.EqualTo("userId claim is missing or invalid"));
    }
    
    [Test]
    public async Task RejectRequest_ShouldReturnError_WhenSomeExceptionOccurs()
    {
        // Arrange 
        var friendshipId = Guid.NewGuid();
        _friendRequestServiceMock
            .Setup(s => s.RejectAsync(friendshipId, _userId))
            .ThrowsAsync(new Exception("error"));

        // Act 
        var result = await _hub.RejectRequest(friendshipId);

        // Assert 
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.ServerError));
        Assert.That(result.ErrorMessage, Is.EqualTo("Unexpected error! Please try later!"));
    }

    [TearDown]
    public void TearDown()
    {
        _hub?.Dispose();
    }
}
