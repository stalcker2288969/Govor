using Govor.Application.Authentication;
using Govor.Application.Groups;
using Govor.Application.Infrastructure.Common;
using Govor.Application.Infrastructure.Validators;
using Govor.Application.Messages;
using Govor.Application.Messages.Parameters;
using Govor.Application.Reactions;
using Govor.Application.Users;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models.Reactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Govor.Domain;
using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartRes;

namespace Govor.Server.Tests;

[TestFixture]
public class GroupTests
{
    private TestDatabase _db = null!;
    private GroupManagementService _groups = null!;
    private Mock<INowDateTimeProvider> _clock = null!;
    private readonly DateTime _now = new(2026, 10, 3, 13, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        _clock = new Mock<INowDateTimeProvider>();
        _clock.SetupGet(c => c.Now).Returns(_now);
        _groups = new GroupManagementService(_db.Context, _clock.Object);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private async Task<Guid> Create(bool isPrivate = false, bool channel = false) =>
        (await _groups.CreateAsync(_db.Alice, "Community", "Description", isPrivate, channel)).Value.Group.Id;

    [Test]
    public async Task PublicSearchDoesNotExposePrivateGroupsOrPrivateHistory()
    {
        var publicId = await Create();
        var privateId = await Create(isPrivate: true);
        Assert.That((await _groups.SearchAsync(_db.Bob, "comm")).Select(g => g.Group.Id), Is.EqualTo(new[] { publicId }));
        Assert.That((await _groups.GetAsync(_db.Bob, privateId)).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _groups.JoinPublicAsync(_db.Bob, privateId)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        var before = await new MessagesLoader(_db.Context).LoadMessagesInChatGroup(publicId, _db.Bob, null);
        Assert.That(before.Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _groups.JoinPublicAsync(_db.Bob, publicId)).Value.MyRole, Is.EqualTo(GroupRole.Member));
        Assert.That((await _groups.MineAsync(_db.Bob)).Single().Group.Id, Is.EqualTo(publicId));
    }

    [Test]
    public async Task InvitationPreviewDoesNotJoinAndUsageDoesNotResetAfterLeaving()
    {
        var id = await Create(isPrivate: true);
        var invite = (await _groups.CreateInvitationAsync(_db.Alice, id, 1, 1, "one use")).Value;
        Assert.That((await _groups.PreviewInvitationAsync(_db.Bob, invite.InvitationCode)).IsSuccess, Is.True);
        Assert.That(await _db.Context.GroupMemberships.CountAsync(), Is.EqualTo(1));
        await _groups.JoinByInvitationAsync(_db.Bob, invite.InvitationCode);
        await _groups.JoinByInvitationAsync(_db.Bob, invite.InvitationCode);
        Assert.That(await _db.Context.GroupMemberships.CountAsync(), Is.EqualTo(2));
        Assert.That((await _groups.InvitationsAsync(_db.Alice, id)).Value.Single().UsedCount, Is.EqualTo(1));
        Assert.That((await _groups.JoinByInvitationAsync(_db.Outsider, invite.InvitationCode)).Error.Type, Is.EqualTo(ErrorType.Validation));
        await _groups.LeaveAsync(_db.Bob, id);
        Assert.That((await _groups.JoinByInvitationAsync(_db.Bob, invite.InvitationCode)).IsFailure, Is.True);
    }

    [Test]
    public async Task ExpiredAndRevokedInvitationsAreRejectedAndMembersCannotIssueThem()
    {
        var id = await Create();
        await _groups.JoinPublicAsync(_db.Bob, id);
        Assert.That((await _groups.CreateInvitationAsync(_db.Bob, id, 7, 0, "")).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        var invite = (await _groups.CreateInvitationAsync(_db.Alice, id, 1, 0, "")).Value;
        await _groups.RevokeInvitationAsync(_db.Alice, id, invite.Id);
        Assert.That((await _groups.JoinByInvitationAsync(_db.Outsider, invite.InvitationCode)).IsFailure, Is.True);
        var expired = (await _groups.CreateInvitationAsync(_db.Alice, id, 1, 0, "")).Value;
        _clock.SetupGet(c => c.Now).Returns(_now.AddDays(2));
        Assert.That((await _groups.JoinByInvitationAsync(_db.Outsider, expired.InvitationCode)).IsFailure, Is.True);
    }

    [Test]
    public async Task OwnerControlsProfileAndAdminsCannotEscalateOrBanOwner()
    {
        var id = await Create();
        await _groups.JoinPublicAsync(_db.Bob, id);
        await _groups.JoinPublicAsync(_db.Outsider, id);
        Assert.That((await _groups.SetAdministratorAsync(_db.Bob, id, _db.Bob, true)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        await _groups.SetAdministratorAsync(_db.Alice, id, _db.Bob, true);
        Assert.That((await _groups.GetAsync(_db.Bob, id)).Value.MyRole, Is.EqualTo(GroupRole.Admin));
        Assert.That((await _groups.UpdateAsync(_db.Bob, id, "Hijacked", "", true)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _groups.SetBanAsync(_db.Bob, id, _db.Alice, true)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        await _groups.SetAdministratorAsync(_db.Alice, id, _db.Outsider, true);
        Assert.That((await _groups.SetBanAsync(_db.Bob, id, _db.Outsider, true)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _groups.UpdateAsync(_db.Alice, id, "Renamed", "new", true)).Value.Group.Name, Is.EqualTo("Renamed"));
        Assert.That(await _groups.SearchAsync(_db.Bob, ""), Is.Empty);
    }

    [Test]
    public async Task BanBlocksRejoiningHistoryPostingReadingAndReactingAndRemovesAdminRole()
    {
        var id = await Create();
        await _groups.JoinPublicAsync(_db.Bob, id);
        await _groups.SetAdministratorAsync(_db.Alice, id, _db.Bob, true);
        var invite = (await _groups.CreateInvitationAsync(_db.Alice, id, 7, 0, "")).Value;
        var message = _db.AddMessage(chat: id, type: RecipientType.Group);
        await _groups.SetBanAsync(_db.Alice, id, _db.Bob, true);
        Assert.That(await _db.Context.GroupAdmins.AnyAsync(a => a.UserId == _db.Bob && a.GroupId == id), Is.False);
        Assert.That((await _groups.JoinPublicAsync(_db.Bob, id)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _groups.JoinByInvitationAsync(_db.Bob, invite.InvitationCode)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await new MessagesLoader(_db.Context).LoadMessagesInChatGroup(id, _db.Bob, null)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await Sender().SendMessageAsync(new SendMessage("test", null, id, RecipientType.Group, _db.Bob, _now, []))).IsSuccess, Is.False);
        var reader = new MessageReadingService(_db.Context, NullLogger<MessageReadingService>.Instance, _clock.Object);
        Assert.That((await reader.ReadMessageAsync(_db.Bob, message.Id)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        var reactions = new MessageReactionService(_db.Context, _clock.Object);
        Assert.That((await reactions.SetAsync(_db.Bob, message.Id, DefaultReactionPack.Items()[0].Id)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await _groups.MembersAsync(_db.Alice, id, true, 0, 50)).Value.Single().UserId, Is.EqualTo(_db.Bob));
        await _groups.SetBanAsync(_db.Alice, id, _db.Bob, false);
        Assert.That((await _groups.GetAsync(_db.Bob, id)).Value.MyRole, Is.EqualTo(GroupRole.Member));
        Assert.That((await _groups.MineAsync(_db.Bob)).Count, Is.EqualTo(1));
    }

    [Test]
    public async Task OwnerAndAdminCanPostInChannelAndDemotionRevokesPostingAndPolicy()
    {
        var id = await Create(channel: true);
        await _groups.JoinPublicAsync(_db.Bob, id);
        Assert.That((await Sender().SendMessageAsync(new SendMessage("owner", null, id, RecipientType.Group, _db.Alice, _now, []))).IsSuccess, Is.True);
        Assert.That((await Sender().SendMessageAsync(new SendMessage("member", null, id, RecipientType.Group, _db.Bob, _now, []))).IsSuccess, Is.False);
        var reactions = new MessageReactionService(_db.Context, _clock.Object);
        Assert.That((await reactions.SetPolicyAsync(_db.Alice, id, ChannelReactionMode.Selected, new[] { DefaultReactionPack.Items()[0].Id })).IsSuccess, Is.True);
        await _groups.SetAdministratorAsync(_db.Alice, id, _db.Bob, true);
        var post = await Sender().SendMessageAsync(new SendMessage("admin", null, id, RecipientType.Group, _db.Bob, _now, []));
        Assert.That(post.IsSuccess, Is.True);
        await _groups.SetAdministratorAsync(_db.Alice, id, _db.Bob, false);
        Assert.That((await Sender().SendMessageAsync(new SendMessage("demoted", null, id, RecipientType.Group, _db.Bob, _now, []))).IsSuccess, Is.False);
        Assert.That((await reactions.SetPolicyAsync(_db.Bob, id, ChannelReactionMode.None, Array.Empty<Guid>())).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        var editor = new MessageEditingService(_db.Context, NullLogger<MessageEditingService>.Instance, Options.Create(new MessageEditingOptions()));
        Assert.That((await editor.EditMessageAsync(new EditMessage(_db.Bob, post.Message.Id, "demoted edit", _now))).IsSuccess, Is.False);
    }

    [Test]
    public async Task GroupAdminsCanDeleteMembersMessagesButMembersCannotDeleteOthers()
    {
        var id = await Create();
        await _groups.JoinPublicAsync(_db.Bob, id);
        await _groups.JoinPublicAsync(_db.Outsider, id);
        var message = _db.AddMessage(sender: _db.Outsider, chat: id, type: RecipientType.Group);
        var remover = new MessageRemovingService(_db.Context, NullLogger<MessageRemovingService>.Instance);
        Assert.That((await remover.DeleteMessageAsync(new DeleteMessage(_db.Bob, message.Id, true))).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        await _groups.SetAdministratorAsync(_db.Alice, id, _db.Bob, true);
        await _groups.SetBanAsync(_db.Bob, id, _db.Outsider, true);
        Assert.That((await remover.DeleteMessageAsync(new DeleteMessage(_db.Bob, message.Id, true))).IsSuccess, Is.True);
        Assert.That(await _db.Context.Messages.AnyAsync(m => m.Id == message.Id), Is.False);
    }

    [Test]
    public async Task OwnershipTransferKeepsOwnerUntilAnActiveSuccessorIsChosen()
    {
        var id = await Create();
        Assert.That((await _groups.LeaveAsync(_db.Alice, id)).Error.Type, Is.EqualTo(ErrorType.Validation));
        Assert.That((await _groups.TransferOwnershipAsync(_db.Alice, id, _db.Bob)).IsFailure, Is.True);
        await _groups.JoinPublicAsync(_db.Bob, id);
        await _groups.TransferOwnershipAsync(_db.Alice, id, _db.Bob);
        Assert.That((await _groups.GetAsync(_db.Bob, id)).Value.MyRole, Is.EqualTo(GroupRole.Owner));
        Assert.That((await _groups.GetAsync(_db.Alice, id)).Value.MyRole, Is.EqualTo(GroupRole.Admin));
        Assert.That((await _groups.LeaveAsync(_db.Alice, id)).IsSuccess, Is.True);
        Assert.That(await _db.Context.IsGroupAdministratorAsync(id, _db.Alice), Is.False);
    }

    [Test]
    public async Task ServerAdminConfiguresRequiredChannelAndRegistrationJoinsAtomically()
    {
        var id = await Create(isPrivate: true, channel: true);
        Assert.That((await _groups.SetRequiredChannelAsync(_db.Bob, id, false)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        ReactionTests.MakeAdmin(_db.Context, _db.Alice);
        await _groups.SetRequiredChannelAsync(_db.Alice, id, false);
        var invite = _db.Context.Invitations.Single(i => i.Code == "test");
        var registered = await Account().RegistrationAsync("NewUser", "password", invite);
        Assert.That(registered.IsSuccess, Is.True);
        Assert.That(registered.Value.WasOnline, Is.EqualTo(_now));
        Assert.That((await _groups.MineAsync(registered.Value.Id)).Single().Group.Id, Is.EqualTo(id));
        Assert.That((await _groups.GetAsync(registered.Value.Id, id)).Value.CanLeave, Is.False);
        Assert.That((await _groups.LeaveAsync(registered.Value.Id, id)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        await _groups.SetRequiredChannelAsync(_db.Alice, id, true);
        Assert.That((await _groups.LeaveAsync(registered.Value.Id, id)).IsSuccess, Is.True);
        Assert.That(await _db.Context.GroupMemberships.AnyAsync(m => m.UserId == _db.Bob && m.GroupId == id), Is.False);
    }

    [Test]
    public async Task RequiredChannelRejectsOrdinaryGroupAndCanBeDisabledForNewRegistrations()
    {
        ReactionTests.MakeAdmin(_db.Context, _db.Alice);
        var groupId = await Create();
        Assert.That((await _groups.SetRequiredChannelAsync(_db.Alice, groupId, false)).Error.Type, Is.EqualTo(ErrorType.Validation));
        var channelId = await Create(channel: true);
        await _groups.SetRequiredChannelAsync(_db.Alice, channelId, false);
        await _groups.SetRequiredChannelAsync(_db.Alice, null, true);
        var registered = await Account().RegistrationAsync("OtherUser", "password", _db.Context.Invitations.Single(i => i.Code == "test"));
        Assert.That(registered.IsSuccess, Is.True);
        Assert.That(await _db.Context.GroupMemberships.AnyAsync(m => m.UserId == registered.Value.Id), Is.False);
    }

    [TestCase(0)]
    [TestCase(2)]
    public async Task RegistrationFailureDoesNotConsumeInvitationOrCreateMembership(int maxParticipants)
    {
        ReactionTests.MakeAdmin(_db.Context, _db.Alice);
        var id = await Create(channel: true);
        await _groups.SetRequiredChannelAsync(_db.Alice, id, false);
        var invite = _db.Context.Invitations.Single(i => i.Code == "test");
        await _db.Context.Invitations.Where(i => i.Id == invite.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.MaxParticipants, maxParticipants));
        var registered = await Account().RegistrationAsync("Unavailable", "password", invite);
        Assert.That(registered.IsFailure, Is.True);
        Assert.That(await _db.Context.Users.AnyAsync(u => u.Username == "Unavailable"), Is.False);
        Assert.That(await _db.Context.GroupMemberships.CountAsync(m => m.GroupId == id), Is.EqualTo(1));
        Assert.That(await _db.Context.Invitations.Where(i => i.Id == invite.Id).Select(i => i.Participants).SingleAsync(), Is.Zero);
    }

    private MessageSendingService Sender() => new(_db.Context, NullLogger<MessageSendingService>.Instance);

    [Test]
    public async Task RequiredMembershipWriteFailureRollsBackAccountAndInvitation()
    {
        ReactionTests.MakeAdmin(_db.Context, _db.Alice);
        var id = await Create(channel: true);
        await _groups.SetRequiredChannelAsync(_db.Alice, id, false);
        var invite = _db.Context.Invitations.Single(i => i.Code == "test");
        using var failing = _db.CreateContext(new FailMembershipInsert());
        Assert.ThrowsAsync<DbUpdateException>(async () =>
            await Account(failing).RegistrationAsync("FailedUser", "password", invite));
        Assert.That(await _db.Context.Users.AnyAsync(u => u.Username == "FailedUser"), Is.False);
        Assert.That(await _db.Context.Invitations.Where(i => i.Id == invite.Id).Select(i => i.Participants).SingleAsync(), Is.Zero);
        Assert.That(await _db.Context.GroupMemberships.CountAsync(m => m.GroupId == id), Is.EqualTo(1));
    }

    private sealed class FailMembershipInsert : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"GroupMemberships\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Simulated required membership persistence failure.");
            return ValueTask.FromResult(result);
        }
    }

    private AuthService Account(GovorDbContext? context = null)
    {
        var username = new Mock<IUsernameValidator>();
        username.Setup(v => v.Validate(It.IsAny<string>())).Returns(Govor.Domain.Common.Result.Success());
        var exists = new Mock<IUserNameExistValidator>();
        exists.Setup(v => v.IsUsernameExistsAsync(It.IsAny<string>())).ReturnsAsync(false);
        var hash = new Mock<IPasswordHasher>();
        hash.Setup(h => h.Hash(It.IsAny<string>())).Returns("test hash");
        return new AuthService(context ?? _db.Context, exists.Object, hash.Object, username.Object, _clock.Object);
    }
}
