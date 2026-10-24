using Govor.API.Hubs.Infrastructure;

namespace Govor.API.Tests.Unit.Hubs.Infrastructure;

[TestFixture]
public class ConnectionStoreTests
{
    private ConnectionStore _store = null!;
    [SetUp]
    public void Setup()
    {
        _store = new ConnectionStore();
    }

    [Test]
    public void AddConnection_AddsConnection()
    {
        // Arrange 
        var userId = Guid.NewGuid();
        var connectionId = "conn1";
        
        // Act
        _store.AddConnection(userId, connectionId);

        var connections = _store.GetConnections(userId);
        
        // Assert 
        Assert.That(connections, Contains.Item(connectionId));
    }

    [Test]
    public void AddConnection_DoesNotDuplicateConnection()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var connectionId = "conn1";
        
        // Act 
        _store.AddConnection(userId, connectionId);
        _store.AddConnection(userId, connectionId);

        var connections = _store.GetConnections(userId).ToList();
        
        // Assert 
        Assert.That(connections.Count, Is.EqualTo(1));
    }

    [Test]
    public void RemoveConnection_RemovesConnection()
    {
        // Arrange 
        var userId = Guid.NewGuid();
        var connectionId = "conn1";
        
        // Act
        _store.AddConnection(userId, connectionId);
        _store.RemoveConnection(userId, connectionId);

        var connections = _store.GetConnections(userId);
        // Assert 
        Assert.That(connections, Is.Empty);
    }

    [Test]
    public void RemoveConnection_LastConnection_RemovesUserEntry()
    {   
        // Arrange 
        var userId = Guid.NewGuid();
        var connectionId = "conn1";
        
        // Act 
        _store.AddConnection(userId, connectionId);
        _store.RemoveConnection(userId, connectionId);

        var connections = _store.GetConnections(userId);
        
        // Assert 
        Assert.That(connections, Is.Empty);
    }

    [Test]
    public void RemoveConnection_NonExistingUser_DoesNotThrow()
    {   
        // Arrange 
        var userId = Guid.NewGuid();
        
        // Act & Assert 
        Assert.DoesNotThrow(() =>
            _store.RemoveConnection(userId, "conn1"));
    }

    [Test]
    public void GetConnections_NonExistingUser_ReturnsEmpty()
    {   
        // Arrange 
        var userId = Guid.NewGuid();
        
        // Act 
        var connections = _store.GetConnections(userId);
        
        // Assert 
        Assert.That(connections, Is.Empty);
    }

    [Test]
    public void MultipleUsers_IsolatedConnections()
    {   
        // Arrange 
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        // Act 
        _store.AddConnection(userA, "A1");
        _store.AddConnection(userB, "B1");

        var connectionsA = _store.GetConnections(userA);
        var connectionsB = _store.GetConnections(userB);

        // Assert 
        Assert.That(connectionsA, Contains.Item("A1"));
        Assert.That(connectionsB, Contains.Item("B1"));
        Assert.That(connectionsA, Does.Not.Contain("B1"));
    }

    [Test]
    public void MultipleConnections_ForSameUser_WorkCorrectly()
    {
        // Arrange 
        var userId = Guid.NewGuid();
        
        // Act 
        _store.AddConnection(userId, "conn1");
        _store.AddConnection(userId, "conn2");

        var connections = _store.GetConnections(userId).ToList();
        
        // Assert 
        Assert.That(connections.Count, Is.EqualTo(2));
        Assert.That(connections, Contains.Item("conn1"));
        Assert.That(connections, Contains.Item("conn2"));
    }
}
