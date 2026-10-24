using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;

namespace Govor.Domain.Tests.Persistence;

[TestFixture]
public class DatabaseConstraintTests
{
    private TestDatabase _db = null!;
    [SetUp] public void SetUp() => _db = new TestDatabase();
    [TearDown] public void TearDown() => _db.Dispose();

    [Test]
    public async Task UsernamesAreUnique()
    {
        var user = await _db.Context.Users.FirstAsync();
        _db.Context.Users.Add(new User { Id = Guid.NewGuid(), Username = user.Username,
            PasswordHash = "test", InviteId = user.InviteId, Description = "" });
        Assert.ThrowsAsync<DbUpdateException>(() => _db.Context.SaveChangesAsync());
    }

    [Test]
    public async Task RefreshTokenHashesAreUnique()
    {
        _db.Context.UserSessions.AddRange(
            new UserSession { UserId = _db.Alice, RefreshTokenHash = "duplicate" },
            new UserSession { UserId = _db.Bob, RefreshTokenHash = "duplicate" });
        Assert.ThrowsAsync<DbUpdateException>(() => _db.Context.SaveChangesAsync());
    }

    [Test]
    public void SessionRequiresExistingUser()
    {
        _db.Context.UserSessions.Add(new UserSession { UserId = Guid.NewGuid(), RefreshTokenHash = "test" });
        Assert.ThrowsAsync<DbUpdateException>(() => _db.Context.SaveChangesAsync());
    }

    [Test]
    public async Task ReadReceiptsAreUniquePerUserAndMessage()
    {
        var message = _db.AddMessage();
        _db.Context.MessageViews.AddRange(
            new MessageView { Id = Guid.NewGuid(), UserId = _db.Bob, MessageId = message.Id, ViewedAt = DateTime.UtcNow },
            new MessageView { Id = Guid.NewGuid(), UserId = _db.Bob, MessageId = message.Id, ViewedAt = DateTime.UtcNow });
        Assert.ThrowsAsync<DbUpdateException>(() => _db.Context.SaveChangesAsync());
    }

    [Test]
    public async Task RemovingMessageCascadesReceiptsReactionsAndAttachments()
    {
        var message = _db.AddMessage();
        var media = new MediaFile { Id = Guid.NewGuid(), UploaderId = _db.Alice, Url = "test.bin", MineType = "application/octet-stream" };
        _db.Context.MediaFiles.Add(media);
        _db.Context.MediaAttachments.Add(new MediaAttachments { Id = Guid.NewGuid(), MessageId = message.Id, MediaFileId = media.Id });
        _db.Context.MessageViews.Add(new MessageView { Id = Guid.NewGuid(), MessageId = message.Id, UserId = _db.Bob, ViewedAt = DateTime.UtcNow });
        _db.Context.MessageReactions.Add(new MessageReaction { Id = Guid.NewGuid(), MessageId = message.Id, UserId = _db.Bob, ReactionCode = "like", ReactedAt = DateTime.UtcNow });
        await _db.Context.SaveChangesAsync();
        _db.Context.ChangeTracker.Clear();
        await _db.Context.Messages.Where(m => m.Id == message.Id).ExecuteDeleteAsync();
        Assert.That(await _db.Context.MediaAttachments.CountAsync(), Is.Zero);
        Assert.That(await _db.Context.MessageViews.CountAsync(), Is.Zero);
        Assert.That(await _db.Context.MessageReactions.CountAsync(), Is.Zero);
        Assert.That(await _db.Context.MediaFiles.AnyAsync(m => m.Id == media.Id), Is.True);
    }

    [Test]
    public async Task GroupMembershipIsUniquePerUserAndGroup()
    {
        var group = new ChatGroup { Id = Guid.NewGuid(), Name = "group", Description = "" };
        _db.Context.ChatGroups.Add(group);
        await _db.Context.SaveChangesAsync();
        _db.Context.GroupMemberships.AddRange(
            new GroupMembership { Id = Guid.NewGuid(), GroupId = group.Id, UserId = _db.Alice },
            new GroupMembership { Id = Guid.NewGuid(), GroupId = group.Id, UserId = _db.Alice });
        Assert.ThrowsAsync<DbUpdateException>(() => _db.Context.SaveChangesAsync());
    }

    [Test]
    public async Task RemovingUserCascadesSessions()
    {
        _db.Context.UserSessions.Add(new UserSession { UserId = _db.Outsider, RefreshTokenHash = "test" });
        await _db.Context.SaveChangesAsync();
        _db.Context.ChangeTracker.Clear();
        await _db.Context.Users.Where(u => u.Id == _db.Outsider).ExecuteDeleteAsync();
        Assert.That(await _db.Context.UserSessions.CountAsync(), Is.Zero);
    }
}
