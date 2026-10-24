using Govor.Application.PrivateUserChats;
using Govor.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Govor.Application.Tests.Integration.Users;

[TestFixture]
public class ReaderTests
{
    [Test]
    public async Task PresenceReturnsPersistedLastSeenOrNullForMissingUser()
    {
        using var db = new TestDatabase();
        var time = new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);
        await db.Context.Users.Where(u => u.Id == db.Alice).ExecuteUpdateAsync(s => s.SetProperty(u => u.WasOnline, time));
        var reader = new UserPresenceReader(db.Context);
        Assert.That(await reader.GetLastSeenAsync(db.Alice), Is.EqualTo(time));
        Assert.That(await reader.GetLastSeenAsync(Guid.NewGuid()), Is.Null);
        Assert.That(await reader.GetLastSeenAsync(Guid.Empty), Is.Null);
    }

    [Test]
    public async Task PrivateChatReaderFiltersBothPositionsAndHandlesMissingChat()
    {
        using var db = new TestDatabase();
        var reader = new UserPrivateChatsGetter(db.Context);
        Assert.That((await reader.GetUserChatsAsync(db.Alice)).Select(c => c.Id), Is.EqualTo(new[] { db.ChatId }));
        Assert.That((await reader.GetUserChatsAsync(db.Bob)).Select(c => c.Id), Is.EqualTo(new[] { db.ChatId }));
        Assert.That(await reader.GetUserChatsAsync(db.Outsider), Is.Empty);
        Assert.That((await reader.GetPrivateChatAsync(db.ChatId)).Value.Id, Is.EqualTo(db.ChatId));
        Assert.That((await reader.GetPrivateChatAsync(Guid.NewGuid())).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That(await reader.ExistChatAsync(db.Alice, db.Bob), Is.True);
        Assert.That(await reader.ExistChatAsync(db.Bob, db.Alice), Is.True);
        Assert.That(await reader.ExistChatAsync(db.Alice, db.Outsider), Is.False);
    }

    [Test]
    public async Task UsernameExistenceHandlesBlankUnknownAndExistingUsernames()
    {
        using var db = new TestDatabase();
        var validator = new Govor.Application.Users.UserNameExistValidator(db.Context);
        Assert.That(await validator.IsUsernameExistsAsync(" "), Is.False);
        Assert.That(await validator.IsUsernameExistsAsync(null!), Is.False);
        Assert.That(await validator.IsUsernameExistsAsync("missing"), Is.False);
        Assert.That(await validator.IsUsernameExistsAsync(db.Alice.ToString()), Is.True);
    }
}

