using Govor.Application.Users.UserOnlineStatus;

namespace Govor.Application.Tests.Users;

[TestFixture]
public class OnlineUserStoreTests
{
    [Test]
    public void UserRemainsOnlineUntilLastConnectionLeaves()
    {
        var store = new OnlineUserStore();
        var user = Guid.NewGuid();
        Assert.That(store.IsOnline(user), Is.False);
        Assert.That(store.AddConnection(user, "phone"), Is.True);
        Assert.That(store.AddConnection(user, "desktop"), Is.False);
        Assert.That(store.AddConnection(user, "phone"), Is.False);
        Assert.That(store.GetConnections(user), Is.EquivalentTo(new[] { "phone", "desktop" }));
        Assert.That(store.GetAllOnlineUsers(), Is.EqualTo(new[] { user }));
        Assert.That(store.RemoveConnection(user, "phone"), Is.False);
        Assert.That(store.IsOnline(user), Is.True);
        Assert.That(store.RemoveConnection(user, "desktop"), Is.True);
        Assert.That(store.IsOnline(user), Is.False);
        Assert.That(store.GetAllOnlineUsers(), Is.Empty);
        Assert.That(store.GetConnections(user), Is.Empty);
    }

    [Test]
    public void ConnectionSnapshotsAndOtherUsersAreIndependent()
    {
        var store = new OnlineUserStore();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        store.AddConnection(alice, "alice");
        var snapshot = store.GetConnections(alice);
        store.AddConnection(alice, "second");
        store.AddConnection(bob, "bob");
        store.RemoveConnection(alice, "alice");
        store.RemoveConnection(alice, "second");
        Assert.That(snapshot, Is.EqualTo(new[] { "alice" }));
        Assert.That(store.IsOnline(bob), Is.True);
        Assert.That(store.RemoveConnection(Guid.NewGuid(), "unknown"), Is.False);
    }
}
