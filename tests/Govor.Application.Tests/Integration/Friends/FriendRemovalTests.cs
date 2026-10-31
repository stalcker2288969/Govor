using Govor.Application.Friends;
using Govor.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Govor.Application.Tests.Integration.Friends;

[TestFixture]
public sealed class FriendRemovalTests
{
    [TestCase(FriendshipStatus.Pending)]
    [TestCase(FriendshipStatus.Blocked)]
    [TestCase(FriendshipStatus.Rejected)]
    [TestCase(FriendshipStatus.Accepted)]
    public async Task RemovalChangesOnlyAcceptedFriendshipAndRetainsHistory(FriendshipStatus status)
    {
        using var db = new TestDatabase();
        db.Context.Friendships.Add(new Friendship { Id = Guid.NewGuid(), RequesterId = db.Alice, AddresseeId = db.Bob, Status = status });
        await db.Context.SaveChangesAsync();
        var message = db.AddMessage();
        var service = new FriendshipService(db.Context);
        await service.RemoveFriendAsync(db.Bob, db.Alice);
        await service.RemoveFriendAsync(db.Alice, db.Bob);
        Assert.That((await db.Context.Friendships.AsNoTracking().SingleAsync()).Status,
            Is.EqualTo(status == FriendshipStatus.Accepted ? FriendshipStatus.Rejected : status));
        Assert.That(await db.Context.Messages.AnyAsync(m => m.Id == message.Id), Is.True);
        Assert.That(await db.Context.PrivateChats.AnyAsync(c => c.Id == db.ChatId), Is.True);
        Assert.ThrowsAsync<ArgumentException>(() => service.RemoveFriendAsync(db.Alice, db.Alice));
        Assert.ThrowsAsync<ArgumentException>(() => service.RemoveFriendAsync(Guid.Empty, db.Alice));
        Assert.ThrowsAsync<ArgumentException>(() => service.RemoveFriendAsync(db.Alice, Guid.Empty));
    }
}
