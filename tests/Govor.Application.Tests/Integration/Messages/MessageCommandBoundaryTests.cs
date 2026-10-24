using Govor.Application.Messages;
using Govor.Application.Messages.Parameters;
using Govor.Application.Tests.Support;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Govor.Application.Tests.Integration.Messages;

[TestFixture]
public class MessageCommandBoundaryTests
{
    private TestDatabase _db = null!;
    private Guid _group;
    private readonly DateTime _now = new(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
    [SetUp]
    public async Task SetUp()
    {
        _db = new TestDatabase();
        _group = Guid.NewGuid();
        _db.Context.ChatGroups.Add(new ChatGroup { Id = _group, Name = "public", Description = "", OwnerUserId = _db.Alice });
        _db.Context.GroupMemberships.Add(new GroupMembership { Id = Guid.NewGuid(), GroupId = _group, UserId = _db.Alice });
        await _db.Context.SaveChangesAsync();
    }
    [TearDown] public void TearDown() => _db.Dispose();
    private MessageSendingService Sender(GovorDbContext? db = null) => new(db ?? _db.Context, NullLogger<MessageSendingService>.Instance);
    private MessageEditingService Editor(GovorDbContext? db = null, bool enabled = true) => new(db ?? _db.Context,
        NullLogger<MessageEditingService>.Instance, Options.Create(new MessageEditingOptions { Enabled = enabled, MaxEditTimeMinutes = 5 }));
    private Message GroupMessage() => _db.AddMessage(chat: _group, type: RecipientType.Group, sentAt: _now);

    [Test]
    public async Task PublicGroupMessagePersistsAttachmentsAndPushAtomically()
    {
        var file = new MediaFile { Id = Guid.NewGuid(), UploaderId = _db.Alice, Url = "photo.png",
            MineType = "image/png", OwnerType = MediaOwnerType.Message };
        _db.Context.MediaFiles.Add(file);
        await _db.Context.SaveChangesAsync();
        var sent = await Sender().SendMessageAsync(new("public content", null, _group, RecipientType.Group,
            _db.Alice, _now, new[] { new SendMedia(file.Id, "") }));
        Assert.That(sent.IsSuccess, Is.True);
        Assert.That(await _db.Context.MediaAttachments.CountAsync(a => a.MessageId == sent.Message.Id), Is.EqualTo(1));
        Assert.That(await _db.Context.ChatPushNotifications.AnyAsync(p => p.MessageId == sent.Message.Id), Is.True);
    }

    [TestCase("empty")]
    [TestCase("too-long")]
    [TestCase("too-many-files")]
    [TestCase("bad-envelope")]
    [TestCase("bad-type")]
    [TestCase("missing-group")]
    [TestCase("missing-chat")]
    [TestCase("missing-media")]
    [TestCase("foreign-reply")]
    public async Task InvalidSendDoesNotPersistMessageOrPush(string reason)
    {
        var content = reason == "empty" ? "" : reason == "too-long" ? new string('x', Govor.Application.E2ee.E2eeProtocol.MaxEnvelopeLength + 1) :
            reason == "bad-envelope" ? Govor.Application.E2ee.E2eeProtocol.Prefix + "bad" : "content";
        var files = reason == "too-many-files" ? Enumerable.Range(0, 101).Select(_ => new SendMedia(Guid.NewGuid(), "")).ToArray() :
            reason == "missing-media" ? new[] { new SendMedia(Guid.NewGuid(), "") } : Array.Empty<SendMedia>();
        var type = reason == "bad-type" ? (RecipientType)99 : reason == "missing-chat" ? RecipientType.User : RecipientType.Group;
        var target = reason is "missing-group" or "missing-chat" ? Guid.NewGuid() : _group;
        var request = new SendMessage(content, reason == "foreign-reply" ? Guid.NewGuid() : null, target, type, _db.Alice, _now, files);
        Assert.That((await Sender().SendMessageAsync(request)).IsSuccess, Is.False);
        Assert.That(await _db.Context.Messages.CountAsync(), Is.Zero);
        Assert.That(await _db.Context.ChatPushNotifications.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task PublicGroupEditPersistsAndReturnsOriginalContext()
    {
        var message = GroupMessage();
        var result = await Editor().EditMessageAsync(new(_db.Alice, message.Id, "edited", _now.AddMinutes(1)));
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.OriginalMessage!.Id, Is.EqualTo(message.Id));
        var stored = await _db.Context.Messages.AsNoTracking().SingleAsync();
        Assert.That(stored.EncryptedContent, Is.EqualTo("edited"));
        Assert.That(stored.IsEdited, Is.True);
        Assert.That(stored.EditedAt, Is.EqualTo(_now.AddMinutes(1)));
    }

    [TestCase("missing")]
    [TestCase("not-author")]
    [TestCase("expired")]
    [TestCase("blank")]
    [TestCase("null")]
    [TestCase("too-long")]
    public async Task InvalidEditLeavesOriginalMessage(string reason)
    {
        var message = GroupMessage();
        var content = reason == "blank" ? " " : reason == "null" ? null : reason == "too-long" ?
            new string('x', Govor.Application.E2ee.E2eeProtocol.MaxEnvelopeLength + 1) : "edited";
        var result = await Editor().EditMessageAsync(new(reason == "not-author" ? _db.Bob : _db.Alice,
            reason == "missing" ? Guid.NewGuid() : message.Id, content!, reason == "expired" ? _now.AddMinutes(6) : _now.AddMinutes(1)));
        Assert.That(result.IsSuccess, Is.False);
        Assert.That((await _db.Context.Messages.AsNoTracking().SingleAsync()).EncryptedContent, Is.EqualTo("hello"));
    }

    [Test]
    public async Task DisablingEditDeadlinePermitsAnOldPublicGroupMessage()
    {
        var message = GroupMessage();
        Assert.That((await Editor(enabled: false).EditMessageAsync(new(_db.Alice, message.Id, "edited", _now.AddDays(1)))).IsSuccess, Is.True);
    }

    [TestCase("send")]
    [TestCase("edit")]
    [TestCase("delete")]
    public async Task PersistenceFailureRollsBackGroupOperation(string operation)
    {
        var message = operation == "send" ? null : GroupMessage();
        var predicate = operation == "send" ? "INSERT INTO \"ChatPushNotifications\"" :
            operation == "edit" ? "UPDATE \"Messages\"" : "DELETE FROM \"Messages\"";
        using var failed = _db.CreateContext(new FailDatabaseCommand(sql => sql.Contains(predicate, StringComparison.Ordinal)));
        if (operation == "send")
            Assert.ThrowsAsync<DbUpdateException>(() => Sender(failed).SendMessageAsync(new("content", null, _group, RecipientType.Group, _db.Alice, _now, [])));
        else if (operation == "edit")
            Assert.ThrowsAsync<DbUpdateException>(() => Editor(failed).EditMessageAsync(new(_db.Alice, message!.Id, "edited", _now)));
        else
            Assert.ThrowsAsync<DbUpdateException>(() => new MessageRemovingService(failed, NullLogger<MessageRemovingService>.Instance)
                .DeleteMessageAsync(new(_db.Alice, message!.Id, true)));
        Assert.That(await _db.Context.Messages.CountAsync(), Is.EqualTo(operation == "send" ? 0 : 1));
        Assert.That(await _db.Context.ChatPushNotifications.CountAsync(), Is.Zero);
        if (operation == "edit") Assert.That((await _db.Context.Messages.AsNoTracking().SingleAsync()).EncryptedContent, Is.EqualTo("hello"));
        Assert.That(failed.ChangeTracker.Entries(), Is.Empty);
    }

    [Test]
    public async Task DeleteValidatesMissingMessageAndExplicitGlobalRemoval()
    {
        var service = new MessageRemovingService(_db.Context, NullLogger<MessageRemovingService>.Instance);
        Assert.That((await service.DeleteMessageAsync(new(_db.Alice, Guid.NewGuid(), true))).Error.Code, Is.EqualTo("Message.NotFound"));
        var message = GroupMessage();
        Assert.That((await service.DeleteMessageAsync(new(_db.Alice, message.Id, false))).Error.Code, Is.EqualTo("Message.HideNotSupported"));
        Assert.That((await service.DeleteMessageAsync(new(_db.Bob, message.Id, true))).Error.Code, Is.EqualTo("Chat.AccessDenied"));
        Assert.That(await _db.Context.Messages.AnyAsync(m => m.Id == message.Id), Is.True);
    }

    [Test]
    public async Task GroupHistoryUsesMembershipAndBoundaryPagination()
    {
        var messages = Enumerable.Range(0, 3).Select(i => _db.AddMessage(chat: _group, type: RecipientType.Group, sentAt: _now.AddMinutes(i))).ToArray();
        var loader = new MessagesLoader(_db.Context);
        Assert.That((await loader.LoadMessagesInChatGroup(_group, _db.Alice, messages[1].Id, 1, 1)).Value.Select(m => m.Id),
            Is.EqualTo(messages.Select(m => m.Id)));
        Assert.That((await loader.LoadMessagesInChatGroup(_group, _db.Bob, null)).IsFailure, Is.True);
        Assert.That((await loader.LoadMessagesInChatGroup(Guid.Empty, _db.Alice, null)).IsFailure, Is.True);
        Assert.That(await _db.Context.HasChatAccessAsync(Guid.Empty, _group, RecipientType.Group), Is.False);
        Assert.That(await _db.Context.HasChatAccessAsync(_db.Alice, Guid.Empty, RecipientType.Group), Is.False);
        Assert.That(await _db.Context.HasChatAccessAsync(_db.Alice, _group, (RecipientType)99), Is.False);
    }
}

