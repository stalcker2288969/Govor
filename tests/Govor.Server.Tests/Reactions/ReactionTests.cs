using Govor.Application.Infrastructure.Common;
using Govor.Application.Reactions;
using Govor.Application.Storage;
using Govor.Domain;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Reactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Server.Tests;

[TestFixture]
public class ReactionTests
{
    private TestDatabase _db = null!;
    private MessageReactionService _reactions = null!;
    private ReactionPackService _packs = null!;
    private TestReactionStorage _storage = null!;
    private readonly DateTime _now = new(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);
    private Guid First => DefaultReactionPack.Items()[0].Id;
    private Guid Second => DefaultReactionPack.Items()[1].Id;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        var clock = new Mock<INowDateTimeProvider>();
        clock.SetupGet(c => c.Now).Returns(_now);
        _storage = new TestReactionStorage();
        _reactions = new MessageReactionService(_db.Context, clock.Object);
        _packs = new ReactionPackService(_db.Context, clock.Object, new ReactionMediaProcessor(), _storage,
            NullLogger<ReactionPackService>.Instance);
        MakeAdmin(_db.Context, _db.Alice);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task DefaultPackIsAutomaticallyAvailableAndImmutable()
    {
        var mine = await _packs.ListAsync(_db.Bob, installedOnly: true);
        Assert.That(mine.Single().IsDefault, Is.True);
        Assert.That(mine.Single().Reactions.Count, Is.EqualTo(12));
        Assert.That((await _packs.DisableAsync(_db.Alice, DefaultReactionPack.Id)).Error.Type, Is.EqualTo(ErrorType.Validation));
        Assert.That((await _packs.AddEmojiAsync(_db.Alice, DefaultReactionPack.Id, "new", "😎")).IsFailure, Is.True);
        Assert.That((await _packs.SubscribeAsync(_db.Bob, DefaultReactionPack.Id, false)).IsFailure, Is.True);
    }

    [Test]
    public async Task PutIsIdempotentAndReplacementKeepsOneOwnReaction()
    {
        var message = _db.AddMessage();
        var first = await _reactions.SetAsync(_db.Bob, message.Id, First);
        var repeated = await _reactions.SetAsync(_db.Bob, message.Id, First);
        Assert.That(first.Value.Version, Is.EqualTo(1));
        Assert.That(repeated.Value.Changed, Is.False);
        Assert.That(repeated.Value.Version, Is.EqualTo(1));
        Assert.That(repeated.Value.ActorReaction!.ReactedAt, Is.EqualTo(_now));
        var replaced = await _reactions.SetAsync(_db.Bob, message.Id, Second);
        Assert.That(replaced.Value.Version, Is.EqualTo(2));
        Assert.That(replaced.Value.Counts.Single().ReactionId, Is.EqualTo(Second));
        Assert.That(await _db.Context.MessageReactions.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task AggregateCountsAndRemovingOwnReactionDoNotRemoveOtherUsers()
    {
        var message = _db.AddMessage();
        await _reactions.SetAsync(_db.Alice, message.Id, First);
        await _reactions.SetAsync(_db.Bob, message.Id, First);
        var state = await _reactions.GetAsync(_db.Alice, message.Id);
        Assert.That(state.Value.Counts.Single().Count, Is.EqualTo(2));
        var removed = await _reactions.SetAsync(_db.Bob, message.Id, null);
        Assert.That(removed.Value.ActorReaction, Is.Null);
        Assert.That(removed.Value.Counts.Single().Count, Is.EqualTo(1));
        Assert.That((await _reactions.SetAsync(_db.Bob, message.Id, null)).Value.Changed, Is.False);
        Assert.That((await _db.Context.MessageReactions.SingleAsync()).UserId, Is.EqualTo(_db.Alice));
    }

    [Test]
    public async Task OutsiderCannotReadSetOrDeleteReactions()
    {
        var message = _db.AddMessage();
        await _reactions.SetAsync(_db.Alice, message.Id, First);
        foreach (var result in new[]
        {
            await _reactions.GetAsync(_db.Outsider, message.Id),
            await _reactions.SetAsync(_db.Outsider, message.Id, Second),
            await _reactions.SetAsync(_db.Outsider, message.Id, null)
        }) Assert.That(result.Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That(await _db.Context.MessageReactions.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task ChannelAdministratorSelectsAllowedReactionsAndPolicyFailureRollsBack()
    {
        var groupId = Channel();
        var message = _db.AddMessage(chat: groupId, type: Govor.Domain.Models.Messages.RecipientType.Group);
        var selected = await _reactions.SetPolicyAsync(_db.Alice, groupId, ChannelReactionMode.Selected, new[] { First });
        Assert.That(selected.Value.Version, Is.EqualTo(1));
        Assert.That((await _reactions.SetAsync(_db.Bob, message.Id, First)).IsSuccess, Is.True);
        Assert.That((await _reactions.SetAsync(_db.Bob, message.Id, Second)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        var invalid = await _reactions.SetPolicyAsync(_db.Alice, groupId, ChannelReactionMode.Selected, new[] { Second, Guid.NewGuid() });
        Assert.That(invalid.IsFailure, Is.True);
        var unchanged = await _reactions.GetPolicyAsync(_db.Bob, groupId);
        Assert.That(unchanged.Value.Version, Is.EqualTo(1));
        Assert.That(unchanged.Value.ReactionIds, Is.EqualTo(new[] { First }));
        await _reactions.SetPolicyAsync(_db.Alice, groupId, ChannelReactionMode.None, Array.Empty<Guid>());
        Assert.That((await _reactions.SetAsync(_db.Bob, message.Id, First)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        // Restricting new reactions does not erase history; users can still remove their own.
        Assert.That((await _reactions.SetAsync(_db.Bob, message.Id, null)).IsSuccess, Is.True);
    }

    [Test]
    public async Task OrdinaryAndBannedMembersCannotChangeChannelPolicy()
    {
        var group = Channel();
        Assert.That((await _reactions.SetPolicyAsync(_db.Bob, group, ChannelReactionMode.None, Array.Empty<Guid>())).Error.Type,
            Is.EqualTo(ErrorType.Forbidden));
        await _db.Context.GroupMemberships.Where(m => m.GroupId == group && m.UserId == _db.Alice)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsBanned, true));
        Assert.That((await _reactions.SetPolicyAsync(_db.Alice, group, ChannelReactionMode.All, Array.Empty<Guid>())).Error.Type,
            Is.EqualTo(ErrorType.Forbidden));
        var message = _db.AddMessage(chat: group, type: Govor.Domain.Models.Messages.RecipientType.Group);
        Assert.That((await _reactions.SetAsync(_db.Alice, message.Id, First)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
    }

    [Test]
    public async Task UsersCanShareInstallAndUninstallAnAdminCreatedPack()
    {
        Assert.That((await _packs.CreateAsync(_db.Bob, "forbidden", "")).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        var pack = (await _packs.CreateAsync(_db.Alice, "Custom", "description")).Value;
        var item = (await _packs.AddEmojiAsync(_db.Alice, pack.Id, "cool", "😎")).Value;
        Assert.That((await _packs.GetSharedAsync(pack.ShareCode)).Value.Reactions.Single().Id, Is.EqualTo(item.Id));
        await _packs.SubscribeAsync(_db.Bob, pack.Id, true);
        await _packs.SubscribeAsync(_db.Bob, pack.Id, true);
        Assert.That(await _db.Context.UserReactionPacks.CountAsync(), Is.EqualTo(1));
        Assert.That((await _packs.ListAsync(_db.Bob, installedOnly: true)).Count, Is.EqualTo(2));
        await _packs.SubscribeAsync(_db.Bob, pack.Id, false);
        Assert.That((await _packs.ListAsync(_db.Bob, installedOnly: true)).Count, Is.EqualTo(1));
        // Installation affects the picker, not who may use a publicly shared reaction.
        Assert.That((await _reactions.SetAsync(_db.Bob, _db.AddMessage().Id, item.Id)).IsSuccess, Is.True);
    }

    [Test]
    public async Task DisabledPackBlocksNewReactionsButPreservesHistoryAndDefinition()
    {
        var pack = (await _packs.CreateAsync(_db.Alice, "Custom", "")).Value;
        var item = (await _packs.AddEmojiAsync(_db.Alice, pack.Id, "cool", "😎")).Value;
        var message = _db.AddMessage();
        await _reactions.SetAsync(_db.Bob, message.Id, item.Id);
        await _packs.DisableAsync(_db.Alice, pack.Id);
        Assert.That((await _reactions.SetAsync(_db.Alice, message.Id, item.Id)).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _reactions.GetAsync(_db.Bob, message.Id)).Value.Counts.Single().ReactionId, Is.EqualTo(item.Id));
        Assert.That((await _packs.GetReactionAsync(item.Id)).IsSuccess, Is.True);
        Assert.That((await _packs.GetSharedAsync(pack.ShareCode)).IsFailure, Is.True);
    }

    public static void MakeAdmin(GovorDbContext context, Guid userId)
    {
        var invite = new Invitation { Id = Guid.NewGuid(), Code = Guid.NewGuid().ToString("N"), IsAdmin = true,
            Description = "admin test", EndDate = DateTime.UtcNow.AddDays(1), MaxParticipants = 10 };
        context.Invitations.Add(invite);
        context.Users.Single(u => u.Id == userId).InviteId = invite.Id;
        context.SaveChanges();
    }

    private Guid Channel()
    {
        var group = new ChatGroup { Id = Guid.NewGuid(), Name = "channel", Description = "", IsChannel = true };
        _db.Context.ChatGroups.Add(group);
        foreach (var user in new[] { _db.Alice, _db.Bob })
            _db.Context.GroupMemberships.Add(new GroupMembership { Id = Guid.NewGuid(), GroupId = group.Id, UserId = user });
        _db.Context.GroupAdmins.Add(new GroupAdmins { Id = Guid.NewGuid(), GroupId = group.Id, UserId = _db.Alice });
        _db.Context.SaveChanges();
        return group.Id;
    }
}

public class TestReactionStorage : IStorageService
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public Task<string> SaveAsync(byte[] data, string fileName)
    {
        var path = Guid.NewGuid().ToString("N") + fileName;
        Files.Add(path, data.ToArray());
        return Task.FromResult(path);
    }
    public Task<Stream> LoadAsync(string url) => Task.FromResult<Stream>(new MemoryStream(Files[url], writable: false));
    public Task RemoveAsync(string url) { Files.Remove(url); return Task.CompletedTask; }
}
