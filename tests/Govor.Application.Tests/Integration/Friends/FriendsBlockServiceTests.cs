using Govor.Application.Messages;
using Govor.Application.PrivateUserChats;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Microsoft.EntityFrameworkCore;

namespace Govor.Application.Tests.Integration.Friends;

[TestFixture]
public class FriendsBlockServiceTests
{
    private TestDatabase _db = null!;
    private FriendsBlockService _service = null!;
    [SetUp] public void SetUp() { _db = new TestDatabase(); _service = new(_db.Context); }
    [TearDown] public void TearDown() => _db.Dispose();

    [TestCase(false)]
    [TestCase(true)]
    public async Task BlockCreatesOrUpdatesRelationshipAndPreventsNewMessages(bool existing)
    {
        Guid? original = null;
        if (existing)
        {
            var friend = new Friendship { Id = Guid.NewGuid(), RequesterId = _db.Bob, AddresseeId = _db.Alice, Status = FriendshipStatus.Accepted };
            _db.Context.Friendships.Add(friend);
            await _db.Context.SaveChangesAsync();
            original = friend.Id;
        }
        await _service.BlockFriendRequestAsync(_db.Bob, _db.Alice);
        await _service.BlockFriendRequestAsync(_db.Bob, _db.Alice);
        _db.Context.ChangeTracker.Clear();
        var block = await _db.Context.Friendships.SingleAsync();
        Assert.That(block.RequesterId, Is.EqualTo(_db.Alice));
        Assert.That(block.AddresseeId, Is.EqualTo(_db.Bob));
        Assert.That(block.Status, Is.EqualTo(FriendshipStatus.Blocked));
        if (original.HasValue) Assert.That(block.Id, Is.EqualTo(original.Value));
        var sender = new MessageSendingService(_db.Context, Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageSendingService>.Instance);
        var sent = await sender.SendMessageAsync(new("hello", null, _db.ChatId, RecipientType.User, _db.Bob, DateTime.UtcNow, []));
        Assert.That(sent.IsSuccess, Is.False);
        Assert.That(sent.Exception, Is.TypeOf<UnauthorizedAccessException>());
        var requests = new FriendRequestCommandService(_db.Context, Moq.Mock.Of<IUserPrivateChatsCreator>());
        Assert.That((await requests.SendAsync(_db.Bob, _db.Alice)).IsFailure, Is.True);
    }

    [Test]
    public async Task OnlyBlockOwnerCanRemoveOrReplaceBlockAndUnblockAllowsNewRequest()
    {
        await _service.BlockFriendRequestAsync(_db.Bob, _db.Alice);
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.UnblockFriendRequestAsync(_db.Alice, _db.Bob));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.BlockFriendRequestAsync(_db.Alice, _db.Bob));
        await _service.UnblockFriendRequestAsync(_db.Bob, _db.Alice);
        await _service.UnblockFriendRequestAsync(_db.Bob, _db.Alice);
        Assert.That((await _db.Context.Friendships.SingleAsync()).Status, Is.EqualTo(FriendshipStatus.Rejected));
        var requests = new FriendRequestCommandService(_db.Context, Moq.Mock.Of<IUserPrivateChatsCreator>());
        Assert.That((await requests.SendAsync(_db.Alice, _db.Bob)).IsSuccess, Is.True);
        Assert.That((await _db.Context.Friendships.SingleAsync()).Status, Is.EqualTo(FriendshipStatus.Pending));
    }

    [Test]
    public async Task UnblockingMissingOrUnblockedRelationshipDoesNotChangeFriendship()
    {
        await _service.UnblockFriendRequestAsync(_db.Bob, _db.Alice);
        _db.Context.Friendships.Add(new Friendship { Id = Guid.NewGuid(), RequesterId = _db.Alice,
            AddresseeId = _db.Bob, Status = FriendshipStatus.Accepted });
        await _db.Context.SaveChangesAsync();
        await _service.UnblockFriendRequestAsync(_db.Bob, _db.Alice);
        Assert.That((await _db.Context.Friendships.SingleAsync()).Status, Is.EqualTo(FriendshipStatus.Accepted));
    }

    [Test]
    public void InvalidOrMissingUsersCannotBeBlocked()
    {
        Assert.ThrowsAsync<ArgumentException>(() => _service.BlockFriendRequestAsync(_db.Alice, _db.Alice));
        Assert.ThrowsAsync<ArgumentException>(() => _service.BlockFriendRequestAsync(Guid.Empty, _db.Alice));
        Assert.ThrowsAsync<ArgumentException>(() => _service.UnblockFriendRequestAsync(_db.Alice, Guid.Empty));
        Assert.ThrowsAsync<ArgumentException>(() => _service.UnblockFriendRequestAsync(_db.Alice, _db.Alice));
        Assert.ThrowsAsync<KeyNotFoundException>(() => _service.BlockFriendRequestAsync(Guid.NewGuid(), _db.Alice));
    }
}

