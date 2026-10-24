using Govor.API.Hubs;
using Govor.API.Hubs.Infrastructure;
using Govor.Application.Infrastructure.Extensions;
using Govor.Domain.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace Govor.API.Tests.Unit.Hubs.Infrastructure;

[TestFixture]
public class PrivateChatGroupManagerTests
{
    private Mock<IConnectionStore> _connectionStoreMock = null!;
    private Mock<IHubContext<ChatsHub>> _hubContextMock = null!;
    private Mock<IGroupManager> _groupsMock = null!;
    private PrivateChatGroupManager _service = null!;
    [SetUp]
    public void Setup()
    {
        _connectionStoreMock = new Mock<IConnectionStore>();

        _groupsMock = new Mock<IGroupManager>();

        _hubContextMock = new Mock<IHubContext<ChatsHub>>();
        _hubContextMock
            .SetupGet(h => h.Groups)
            .Returns(_groupsMock.Object);

        _service = new PrivateChatGroupManager(
            _connectionStoreMock.Object,
            _hubContextMock.Object);
    }

    [Test]
    public async Task AddUsersToPrivateChatGroupAsync_AddsAllConnectionsToGroup()
    {
        // Arrange
        var chat = new PrivateChat
        {
            Id = Guid.NewGuid(),
            UserAId = Guid.NewGuid(),
            UserBId = Guid.NewGuid()
        };

        var connectionsA = new List<string> { "connA1", "connA2" };
        var connectionsB = new List<string> { "connB1" };

        _connectionStoreMock
            .Setup(s => s.GetConnections(chat.UserAId))
            .Returns(connectionsA);

        _connectionStoreMock
            .Setup(s => s.GetConnections(chat.UserBId))
            .Returns(connectionsB);

        var expectedGroup = ChatHubConstants.GetPrivateChat(chat.Id);

        // Act
        await _service.AddUsersToPrivateChatGroupAsync(chat);

        // Assert
        foreach (var conn in connectionsA.Concat(connectionsB))
        {
            _groupsMock.Verify(
                g => g.AddToGroupAsync(conn, expectedGroup, default),
                Times.Once);
        }
    }

    [Test]
    public async Task RemoveUsersFromPrivateChatGroupAsync_RemovesAllConnectionsFromGroup()
    {
        // Arrange
        var chat = new PrivateChat
        {
            Id = Guid.NewGuid(),
            UserAId = Guid.NewGuid(),
            UserBId = Guid.NewGuid()
        };

        var connectionsA = new List<string> { "connA1" };
        var connectionsB = new List<string> { "connB1", "connB2" };

        _connectionStoreMock
            .Setup(s => s.GetConnections(chat.UserAId))
            .Returns(connectionsA);

        _connectionStoreMock
            .Setup(s => s.GetConnections(chat.UserBId))
            .Returns(connectionsB);

        var expectedGroup = ChatHubConstants.GetPrivateChat(chat.Id);

        // Act
        await _service.RemoveUsersFromPrivateChatGroupAsync(chat);

        // Assert
        foreach (var conn in connectionsA.Concat(connectionsB))
        {
            _groupsMock.Verify(
                g => g.RemoveFromGroupAsync(conn, expectedGroup, default),
                Times.Once);
        }
    }

    [Test]
    public async Task AddUsersToPrivateChatGroupAsync_NoConnections_DoesNothing()
    {
        // Arrange
        var chat = new PrivateChat
        {
            Id = Guid.NewGuid(),
            UserAId = Guid.NewGuid(),
            UserBId = Guid.NewGuid()
        };

        _connectionStoreMock
            .Setup(s => s.GetConnections(It.IsAny<Guid>()))
            .Returns(new List<string>());

        // Act
        await _service.AddUsersToPrivateChatGroupAsync(chat);

        // Assert
        _groupsMock.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default),
            Times.Never);
    }

    [Test]
    public async Task RemoveUsersFromPrivateChatGroupAsync_NoConnections_DoesNothing()
    {
        // Arrange
        var chat = new PrivateChat
        {
            Id = Guid.NewGuid(),
            UserAId = Guid.NewGuid(),
            UserBId = Guid.NewGuid()
        };

        _connectionStoreMock
            .Setup(s => s.GetConnections(It.IsAny<Guid>()))
            .Returns(new List<string>());

        // Act
        await _service.RemoveUsersFromPrivateChatGroupAsync(chat);

        // Assert
        _groupsMock.Verify(
            g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default),
            Times.Never);
    }
}
