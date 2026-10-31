using Govor.Application.Messages;
using Govor.Application.Messages.Parameters;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Govor.Application.Tests.Integration.Messages;

[TestFixture]
public sealed class ReplyAndForwardTests
{
    private TestDatabase _db = null!;
    private Guid _group;
    private MessageSendingService _sender = null!;
    [SetUp]
    public void SetUp()
    {
        _db = new(); _group = Guid.NewGuid();
        _db.Context.ChatGroups.Add(new ChatGroup { Id = _group, Name = "public", Description = "", OwnerUserId = _db.Alice });
        _db.Context.GroupMemberships.Add(new GroupMembership { Id = Guid.NewGuid(), GroupId = _group, UserId = _db.Alice });
        _db.Context.SaveChanges();
        _sender = new(_db.Context, NullLogger<MessageSendingService>.Instance);
    }
    [TearDown] public void TearDown() => _db.Dispose();
    private SendMessage Request(Guid? reply = null, Guid? forward = null) =>
        new("content", reply, _group, RecipientType.Group, _db.Alice, DateTime.UtcNow, [], forward);

    [Test]
    public async Task ReplyPersistsAndAppearsInHistoryWhileCrossChatReplyIsRejected()
    {
        var source = _db.AddMessage(chat: _group, type: RecipientType.Group);
        var reply = await _sender.SendMessageAsync(Request(reply: source.Id));
        Assert.That(reply.IsSuccess, Is.True);
        var history = await new MessagesLoader(_db.Context).LoadMessagesInChatGroup(_group, _db.Alice, null);
        Assert.That(history.Value.Single(m => m.Id == reply.Message.Id).ReplyToMessageId, Is.EqualTo(source.Id));
        var other = _db.AddMessage();
        Assert.That((await _sender.SendMessageAsync(Request(reply: other.Id))).IsSuccess, Is.False);
        Assert.That((await _sender.SendMessageAsync(Request(reply: Guid.Empty))).IsSuccess, Is.False);
    }

    [Test]
    public async Task ForwardSnapshotsAuthorAndDateSurvivesSourceDeletionAndAppearsInHistory()
    {
        var source = _db.AddMessage(sender: _db.Bob);
        var result = await _sender.SendMessageAsync(Request(forward: source.Id));
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Message.SenderId, Is.EqualTo(_db.Alice));
        Assert.That(result.Message.ForwardedFromUserId, Is.EqualTo(_db.Bob));
        Assert.That(result.Message.ForwardedFromSentAt, Is.EqualTo(source.SentAt));
        await _db.Context.Messages.Where(m => m.Id == source.Id).ExecuteDeleteAsync();
        var history = await new MessagesLoader(_db.Context).LoadMessagesInChatGroup(_group, _db.Alice, null);
        Assert.That(history.Value.Single().ForwardedFromMessageId, Is.EqualTo(source.Id));
        Assert.That(await _db.Context.ChatPushNotifications.AnyAsync(p => p.MessageId == result.Message.Id), Is.True);
    }

    [TestCase("missing")]
    [TestCase("outsider")]
    [TestCase("banned")]
    public async Task CannotForwardMissingOrInaccessibleSource(string reason)
    {
        var otherGroup = Guid.NewGuid();
        _db.Context.ChatGroups.Add(new ChatGroup { Id = otherGroup, Name = "other", Description = "", OwnerUserId = _db.Bob });
        if (reason == "banned")
            _db.Context.GroupMemberships.Add(new GroupMembership { Id = Guid.NewGuid(), GroupId = otherGroup, UserId = _db.Alice, IsBanned = true });
        _db.Context.SaveChanges();
        var id = reason == "missing" ? Guid.NewGuid() : _db.AddMessage(chat: otherGroup, type: RecipientType.Group).Id;
        var before = await _db.Context.Messages.CountAsync();
        var result = await _sender.SendMessageAsync(Request(forward: id));
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(await _db.Context.Messages.CountAsync(), Is.EqualTo(before));
        Assert.That(await _db.Context.ChatPushNotifications.CountAsync(), Is.Zero);
    }
}
