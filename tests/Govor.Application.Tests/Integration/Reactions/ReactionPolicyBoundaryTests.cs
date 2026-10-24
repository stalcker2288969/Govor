using Govor.Application.Infrastructure.Common;
using Govor.Application.Reactions;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Reactions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Govor.Application.Tests.Integration.Reactions;

[TestFixture]
public class ReactionPolicyBoundaryTests
{
    private TestDatabase _db = null!;
    private MessageReactionService _service = null!;
    private Guid _group;
    [SetUp]
    public async Task SetUp()
    {
        _db = new TestDatabase();
        _group = Guid.NewGuid();
        _db.Context.ChatGroups.Add(new ChatGroup { Id = _group, Name = "channel", Description = "", IsChannel = true, OwnerUserId = _db.Alice });
        _db.Context.GroupMemberships.Add(new GroupMembership { Id = Guid.NewGuid(), GroupId = _group, UserId = _db.Alice });
        await _db.Context.SaveChangesAsync();
        _service = new(_db.Context, Mock.Of<INowDateTimeProvider>(c => c.Now == DateTime.UtcNow.Date));
    }
    [TearDown] public void TearDown() => _db.Dispose();

    [TestCase("mode")]
    [TestCase("null")]
    [TestCase("too-many")]
    [TestCase("empty-id")]
    [TestCase("wrong-mode")]
    [TestCase("unavailable")]
    public async Task InvalidPolicyDoesNotChangeModeVersionOrAllowedReactions(string reason)
    {
        var ids = reason == "null" ? null : reason == "too-many" ? Enumerable.Repeat(Guid.NewGuid(), 101).ToArray() :
            reason == "empty-id" ? new[] { Guid.Empty } : new[] { Guid.NewGuid() };
        var mode = reason == "mode" ? (ChannelReactionMode)99 : reason == "wrong-mode" ? ChannelReactionMode.All : ChannelReactionMode.Selected;
        var result = await _service.SetPolicyAsync(_db.Alice, _group, mode, ids!);
        Assert.That(result.Error.Type, Is.EqualTo(ErrorType.Validation));
        var stored = await _db.Context.ChatGroups.AsNoTracking().SingleAsync();
        Assert.That(stored.ReactionPolicyVersion, Is.Zero);
        Assert.That(stored.ReactionMode, Is.EqualTo(ChannelReactionMode.All));
        Assert.That(await _db.Context.ChannelAllowedReactions.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task MissingOrInaccessibleMessageAndGroupCannotBeRead()
    {
        var message = _db.AddMessage();
        Assert.That((await _service.GetAsync(_db.Alice, Guid.NewGuid())).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.GetAsync(_db.Outsider, message.Id)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _service.GetPolicyAsync(_db.Outsider, _group)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _service.SetPolicyAsync(_db.Outsider, _group, ChannelReactionMode.None, [])).IsFailure, Is.True);
    }

    [Test]
    public async Task OrdinaryGroupCannotReceiveChannelPolicy()
    {
        await _db.Context.ChatGroups.Where(g => g.Id == _group).ExecuteUpdateAsync(s => s.SetProperty(g => g.IsChannel, false));
        Assert.That((await _service.SetPolicyAsync(_db.Alice, _group, ChannelReactionMode.None, [])).Error.Code,
            Is.EqualTo("Reaction.Policy.NotChannel"));
    }
}

