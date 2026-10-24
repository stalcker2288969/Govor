using System.Data.Common;
using Govor.Application.Infrastructure.Common;
using Govor.Application.Messages;
using Govor.Domain;
using Govor.Domain.Models.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Server.Tests;

[TestFixture]
public class ReadConcurrencyTests
{
    [Test]
    public async Task AnotherDeviceCanInsertTheReceiptBetweenSelectionAndWrite()
    {
        using var db = new TestDatabase();
        var message = db.AddMessage();
        var clock = new Mock<INowDateTimeProvider>();
        clock.SetupGet(c => c.Now).Returns(DateTime.UtcNow);
        using var other = db.CreateContext();
        var competingReader = new MessageReadingService(other, NullLogger<MessageReadingService>.Instance, clock.Object);
        var interceptor = new BeforeReceiptInsert(async () =>
        {
            var result = await competingReader.ReadMessageAsync(db.Bob, message.Id);
            Assert.That(result.IsSuccess, Is.True);
        });
        using var first = db.CreateContext(interceptor);
        var reader = new MessageReadingService(first, NullLogger<MessageReadingService>.Instance, clock.Object);
        var read = await reader.ReadChatAsync(db.Bob, db.ChatId, RecipientType.User, new[] { message.Id });
        Assert.That(read.IsSuccess, Is.True);
        Assert.That(read.Value.ReadCount, Is.Zero);
        Assert.That(await db.Context.MessageViews.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task MessageRemovedBetweenSelectionAndWriteDoesNotCreateOrphanReceipt()
    {
        using var db = new TestDatabase();
        var message = db.AddMessage();
        var clock = new Mock<INowDateTimeProvider>();
        clock.SetupGet(c => c.Now).Returns(DateTime.UtcNow);
        using var other = db.CreateContext();
        var interceptor = new BeforeReceiptInsert(async () =>
            await other.Messages.Where(m => m.Id == message.Id).ExecuteDeleteAsync());
        using var first = db.CreateContext(interceptor);
        var reader = new MessageReadingService(first, NullLogger<MessageReadingService>.Instance, clock.Object);
        var read = await reader.ReadChatAsync(db.Bob, db.ChatId, RecipientType.User, new[] { message.Id });
        Assert.That(read.IsSuccess, Is.True);
        Assert.That(read.Value.ReadCount, Is.Zero);
        Assert.That(await db.Context.MessageViews.CountAsync(), Is.Zero);
    }

    [Test]
    public void ReadBoundaryQueryTranslatesForPostgreSql()
    {
        using var db = new GovorDbContext(new DbContextOptionsBuilder<GovorDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_tests;Username=tests").Options);
        var boundary = Guid.NewGuid();
        var sentAt = DateTime.UtcNow;
        var sql = db.Messages.Where(m => m.SentAt < sentAt ||
                (m.SentAt == sentAt && m.Id.CompareTo(boundary) <= 0))
            .OrderBy(m => m.SentAt).ThenBy(m => m.Id).ToQueryString();
        Assert.That(sql, Does.Contain("ORDER BY"));
    }

    // Deterministically interleave a competing device/deletion after SELECT, before INSERT.
    private sealed class BeforeReceiptInsert(Func<Task> callback) : DbCommandInterceptor
    {
        private bool _invoked;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_invoked && command.CommandText.StartsWith("INSERT INTO \"MessageViews\"", StringComparison.Ordinal))
            {
                _invoked = true;
                await callback();
            }
            return result;
        }
    }
}
