using Govor.Application.Messages;
using Govor.Domain.Models.Messages;
using Microsoft.EntityFrameworkCore;

namespace Govor.Application.Tests.Messages;

[TestFixture]
public class MessagesLoaderTests
{
    private TestDatabase _db = null!;
    private MessagesLoader _loader = null!;
    [SetUp] public void SetUp() { _db = new TestDatabase(); _loader = new(_db.Context); }
    [TearDown] public void TearDown() => _db.Dispose();

    [Test]
    public async Task LatestPageIsChronologicalAndLimited()
    {
        var start = DateTime.UtcNow.AddHours(-1);
        var messages = Enumerable.Range(0, 5).Select(i => _db.AddMessage(sentAt: start.AddMinutes(i))).ToArray();
        var result = await _loader.LoadMessagesInUserChat(_db.ChatId, _db.Bob, null, 3, 0);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value.Select(m => m.Id), Is.EqualTo(messages.Skip(2).Select(m => m.Id)));
    }

    [Test]
    public async Task AnchorIncludesRequestedMessagesOnBothSides()
    {
        var start = DateTime.UtcNow.AddHours(-1);
        var messages = Enumerable.Range(0, 5).Select(i => _db.AddMessage(sentAt: start.AddMinutes(i))).ToArray();
        var result = await _loader.LoadMessagesInUserChat(_db.ChatId, _db.Alice, messages[2].Id, 1, 1);
        Assert.That(result.Value.Select(m => m.Id), Is.EqualTo(messages.Skip(1).Take(3).Select(m => m.Id)));
    }

    [Test]
    public async Task EqualTimestampsUseIdAsStableTieBreaker()
    {
        var now = DateTime.UtcNow;
        for (var i = 1; i <= 5; i++) _db.AddMessage(sentAt: now, id: Guid.Parse($"00000000-0000-0000-0000-{i:D12}"));
        var expected = await _db.Context.Messages.OrderBy(m => m.Id).Select(m => m.Id).ToArrayAsync();
        var result = await _loader.LoadMessagesInUserChat(_db.ChatId, _db.Bob, expected[2], 1, 1);
        Assert.That(result.Value.Select(m => m.Id), Is.EqualTo(expected.Skip(1).Take(3)));
    }

    [Test]
    public async Task MissingAnchorReturnsEmptyPage()
    {
        _db.AddMessage();
        var result = await _loader.LoadMessagesInUserChat(_db.ChatId, _db.Alice, Guid.NewGuid());
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.Empty);
    }

    [Test]
    public async Task OutsiderCannotLoadPrivateChat() =>
        Assert.That((await _loader.LoadMessagesInUserChat(_db.ChatId, _db.Outsider, null)).IsFailure, Is.True);

    [Test]
    public async Task EmptyConversationIsRejected() =>
        Assert.That((await _loader.LoadMessagesInUserChat(Guid.Empty, _db.Alice, null)).IsFailure, Is.True);
}
