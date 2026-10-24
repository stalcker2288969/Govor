using Govor.Application.Infrastructure.Common;
using Govor.Application.Medias;
using Govor.Application.Messages;
using Govor.Application.Messages.Parameters;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Server.Tests;

[TestFixture]
public class MessageAccessTests
{
    private TestDatabase _db = null!;
    private MessageReadingService _reader = null!;
    private readonly DateTime _now = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        var clock = new Mock<INowDateTimeProvider>();
        clock.SetupGet(c => c.Now).Returns(_now);
        _reader = new MessageReadingService(_db.Context, NullLogger<MessageReadingService>.Instance, clock.Object);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task OutsiderCannotLoadSendReadOrDownloadPrivateMessages()
    {
        // The outsider has a different chat; that must not grant access to Alice–Bob.
        _db.Context.PrivateChats.Add(new PrivateChat
        { Id = Guid.NewGuid(), UserAId = _db.Outsider, UserBId = _db.Alice });
        var message = _db.AddMessage();
        var loader = new MessagesLoader(_db.Context);
        var loaded = await loader.LoadMessagesInUserChat(_db.ChatId, _db.Outsider, null);
        var read = await _reader.ReadMessageAsync(_db.Outsider, message.Id);
        var sender = new MessageSendingService(_db.Context, NullLogger<MessageSendingService>.Instance);
        var sent = await sender.SendMessageAsync(new SendMessage("intrusion", null, _db.ChatId,
            RecipientType.User, _db.Outsider, _now, []));
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Error.Type, Is.EqualTo(ErrorType.Forbidden));
            Assert.That(read.Error.Type, Is.EqualTo(ErrorType.Forbidden));
            Assert.That(sent.IsSuccess, Is.False);
        });
        Assert.That(await _db.Context.MessageViews.CountAsync(), Is.Zero);
        Assert.That(await _db.Context.Messages.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task RepeatedSingleReadReturnsTheSameReceipt()
    {
        var message = _db.AddMessage();
        var first = await _reader.ReadMessageAsync(_db.Bob, message.Id);
        var second = await _reader.ReadMessageAsync(_db.Bob, message.Id);
        Assert.That(first.IsSuccess && second.IsSuccess, Is.True);
        Assert.That(second.Value.MessageViews.Single().Id, Is.EqualTo(first.Value.MessageViews.Single().Id));
        Assert.That(second.Value.MessageViews.Single().ViewedAt, Is.EqualTo(_now));
        Assert.That(await _db.Context.MessageViews.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task VisibleMessageSelectionDoesNotReadOffscreenOrOwnMessages()
    {
        var visible = _db.AddMessage();
        _db.AddMessage();
        _db.AddMessage(_db.Bob);
        var result = await _reader.ReadChatAsync(_db.Bob, _db.ChatId, RecipientType.User, [visible.Id]);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value.ReadCount, Is.EqualTo(1));
        Assert.That(result.Value.UnreadCount, Is.EqualTo(1));
        Assert.That(await _db.Context.MessageViews.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task InvalidMixedChatSelectionDoesNotPartiallyWrite()
    {
        var valid = _db.AddMessage();
        var other = _db.AddMessage(chat: Guid.NewGuid());
        var result = await _reader.ReadChatAsync(_db.Bob, _db.ChatId, RecipientType.User, [valid.Id, other.Id]);
        Assert.That(result.IsFailure, Is.True);
        Assert.That(await _db.Context.MessageViews.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task ReadThroughBoundaryHandlesEqualTimestampsAndLeavesNewerUnread()
    {
        var earlier = _db.AddMessage(sentAt: _now, id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var boundary = _db.AddMessage(sentAt: _now, id: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        _db.AddMessage(sentAt: _now, id: Guid.Parse("00000000-0000-0000-0000-000000000003"));
        _db.AddMessage(sentAt: _now.AddSeconds(1));
        var result = await _reader.ReadChatAsync(_db.Bob, _db.ChatId, RecipientType.User, upToMessageId: boundary.Id);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value.ReadCount, Is.EqualTo(2));
        Assert.That(result.Value.UnreadCount, Is.EqualTo(2));
        Assert.That(await _db.Context.MessageViews.AnyAsync(v => v.MessageId == earlier.Id), Is.True);
    }

    [Test]
    public async Task MarkAllReadsMoreThanOneBatchAndSurvivesRepeatedCall()
    {
        for (var i = 0; i < 205; i++)
            _db.AddMessage(sentAt: _now.AddSeconds(i));
        _db.AddMessage(_db.Bob, sentAt: _now.AddSeconds(300));
        var first = await _reader.ReadChatAsync(_db.Bob, _db.ChatId, RecipientType.User);
        var second = await _reader.ReadChatAsync(_db.Bob, _db.ChatId, RecipientType.User);
        Assert.That(first.Value.ReadCount, Is.EqualTo(205));
        Assert.That(second.Value.ReadCount, Is.Zero);
        Assert.That(second.Value.UnreadCount, Is.Zero);
    }

    [Test]
    public async Task GroupReadRequiresMembershipAndExcludesOwnMessages()
    {
        var group = new ChatGroup { Id = Guid.NewGuid(), Name = "group", Description = "" };
        _db.Context.ChatGroups.Add(group);
        _db.Context.GroupMemberships.Add(new GroupMembership { Id = Guid.NewGuid(), GroupId = group.Id, UserId = _db.Bob });
        _db.Context.SaveChanges();
        _db.AddMessage(chat: group.Id, type: RecipientType.Group);
        _db.AddMessage(_db.Bob, group.Id, RecipientType.Group);
        var denied = await _reader.ReadChatAsync(_db.Outsider, group.Id, RecipientType.Group);
        var read = await _reader.ReadChatAsync(_db.Bob, group.Id, RecipientType.Group);
        Assert.That(denied.Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That(read.Value.ReadCount, Is.EqualTo(1));
        Assert.That(read.Value.UnreadCount, Is.Zero);
    }

    [Test]
    public async Task PrivateAttachmentIsAvailableToBothParticipantsOnly()
    {
        var message = _db.AddMessage();
        var file = new MediaFile { Id = Guid.NewGuid(), UploaderId = _db.Alice,
            Url = "test", MineType = "image/png", OwnerType = MediaOwnerType.Message };
        _db.Context.MediaFiles.Add(file);
        _db.Context.MediaAttachments.Add(new MediaAttachments
        { Id = Guid.NewGuid(), MessageId = message.Id, MediaFileId = file.Id });
        _db.Context.SaveChanges();
        var access = new AccesserToDownloadMediaService(_db.Context);
        Assert.That(await access.HasAccessAsync(file.Id, _db.Alice), Is.True);
        Assert.That(await access.HasAccessAsync(file.Id, _db.Bob), Is.True);
        Assert.That(await access.HasAccessAsync(file.Id, _db.Outsider), Is.False);
    }

    [Test]
    public async Task HideForMeNeverDeletesForEveryone()
    {
        var message = _db.AddMessage();
        var remover = new MessageRemovingService(_db.Context, NullLogger<MessageRemovingService>.Instance);
        var result = await remover.DeleteMessageAsync(new DeleteMessage(_db.Alice, message.Id, false));
        Assert.That(result.IsFailure, Is.True);
        Assert.That(await _db.Context.Messages.AnyAsync(m => m.Id == message.Id), Is.True);
    }

    [Test]
    public async Task SameTimestampHistoryDoesNotSkipMessagesAroundCursor()
    {
        var earlier = _db.AddMessage(sentAt: _now, id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var cursor = _db.AddMessage(sentAt: _now, id: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var later = _db.AddMessage(sentAt: _now, id: Guid.Parse("00000000-0000-0000-0000-000000000003"));
        var result = await new MessagesLoader(_db.Context).LoadMessagesInUserChat(_db.ChatId, _db.Bob, cursor.Id);
        Assert.That(result.Value.Select(m => m.Id), Is.EqualTo(new[] { earlier.Id, cursor.Id, later.Id }));
    }

    [Test]
    public async Task RecipientCannotEditOrDeleteAnotherAuthorsMessage()
    {
        var message = _db.AddMessage(sentAt: _now);
        var editor = new MessageEditingService(_db.Context, NullLogger<MessageEditingService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new MessageEditingOptions()));
        var edited = await editor.EditMessageAsync(new EditMessage(_db.Bob, message.Id, "changed", _now));
        var remover = new MessageRemovingService(_db.Context, NullLogger<MessageRemovingService>.Instance);
        var removed = await remover.DeleteMessageAsync(new DeleteMessage(_db.Bob, message.Id, true));
        Assert.That(edited.Exception, Is.TypeOf<UnauthorizedAccessException>());
        Assert.That(removed.Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _db.Context.Messages.AsNoTracking().SingleAsync()).EncryptedContent, Is.EqualTo("hello"));
    }

    [Test]
    public async Task BannedGroupMemberCannotLoadSendOrReadMessages()
    {
        var group = new ChatGroup { Id = Guid.NewGuid(), Name = "group", Description = "" };
        _db.Context.ChatGroups.Add(group);
        _db.Context.GroupMemberships.Add(new GroupMembership
        { Id = Guid.NewGuid(), GroupId = group.Id, UserId = _db.Bob, IsBanned = true });
        _db.Context.SaveChanges();
        var loaded = await new MessagesLoader(_db.Context).LoadMessagesInChatGroup(group.Id, _db.Bob, null);
        var read = await _reader.ReadChatAsync(_db.Bob, group.Id, RecipientType.Group);
        var sent = await new MessageSendingService(_db.Context, NullLogger<MessageSendingService>.Instance)
            .SendMessageAsync(new SendMessage("hello", null, group.Id, RecipientType.Group, _db.Bob, _now, []));
        Assert.That(loaded.Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That(read.Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That(sent.Exception, Is.TypeOf<UnauthorizedAccessException>());
    }

    [Test]
    public async Task ForeignUploadAndReplyFromAnotherChatCannotBeAttached()
    {
        var file = new MediaFile { Id = Guid.NewGuid(), UploaderId = _db.Bob,
            Url = "test", MineType = "image/png", OwnerType = MediaOwnerType.Message };
        _db.Context.MediaFiles.Add(file);
        _db.Context.SaveChanges();
        var sender = new MessageSendingService(_db.Context, NullLogger<MessageSendingService>.Instance);
        var attachment = await sender.SendMessageAsync(new SendMessage("hello", null, _db.ChatId,
            RecipientType.User, _db.Alice, _now, new[] { new SendMedia(file.Id, "") }));
        var other = _db.AddMessage(chat: Guid.NewGuid());
        var reply = await sender.SendMessageAsync(new SendMessage("hello", other.Id, _db.ChatId,
            RecipientType.User, _db.Alice, _now, []));
        Assert.That(attachment.Exception, Is.TypeOf<UnauthorizedAccessException>());
        Assert.That(reply.Exception, Is.TypeOf<ArgumentException>());
        Assert.That(await _db.Context.ChatPushNotifications.CountAsync(), Is.Zero);
    }
}
