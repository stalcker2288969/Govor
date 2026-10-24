using Govor.Application.Messages;
using Govor.Application.Infrastructure.Common;
using Govor.Domain.Models.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Application.Tests.Integration.Messages;

[TestFixture]
public class ReadBoundaryTests
{
    [Test]
    public async Task EmptyChatReturnsZeroAndMissingBoundaryDoesNotCreateReceipts()
    {
        using var db = new TestDatabase();
        var reader = new MessageReadingService(db.Context, NullLogger<MessageReadingService>.Instance,
            Mock.Of<INowDateTimeProvider>(c => c.Now == DateTime.UtcNow.Date));
        var empty = await reader.ReadChatAsync(db.Bob, db.ChatId, RecipientType.User);
        Assert.That(empty.IsSuccess, Is.True);
        Assert.That(empty.Value.ReadCount, Is.Zero);
        Assert.That(empty.Value.UnreadCount, Is.Zero);
        Assert.That((await reader.ReadChatAsync(db.Bob, db.ChatId, RecipientType.User, upToMessageId: Guid.NewGuid())).Error.Code,
            Is.EqualTo("Message.Read.BoundaryNotFound"));
        Assert.That((await reader.ReadMessageAsync(db.Bob, Guid.NewGuid())).Error.Code, Is.EqualTo("Message.NotFound"));
        Assert.That((await reader.GetUnreadCountAsync(db.Outsider, db.ChatId, RecipientType.User)).IsFailure, Is.True);
        Assert.That(await db.Context.MessageViews.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task InvalidSelectionsAndOwnMessagesCannotBeMarkedRead()
    {
        using var db = new TestDatabase();
        var reader = new MessageReadingService(db.Context, NullLogger<MessageReadingService>.Instance, Mock.Of<INowDateTimeProvider>());
        var own = db.AddMessage(sender: db.Bob);
        Assert.That((await reader.ReadChatAsync(db.Bob, db.ChatId, RecipientType.User, new[] { own.Id })).Error.Code, Is.EqualTo("Message.Read.OwnMessage"));
        foreach (var selection in new[] { Array.Empty<Guid>(), new[] { Guid.Empty }, Enumerable.Repeat(own.Id, 101).ToArray() })
            Assert.That((await reader.ReadChatAsync(db.Bob, db.ChatId, RecipientType.User, selection)).IsFailure, Is.True);
        Assert.That((await reader.ReadChatAsync(db.Bob, db.ChatId, RecipientType.User, new[] { own.Id }, own.Id)).IsFailure, Is.True);
        Assert.That(await db.Context.MessageViews.CountAsync(), Is.Zero);
    }
}

