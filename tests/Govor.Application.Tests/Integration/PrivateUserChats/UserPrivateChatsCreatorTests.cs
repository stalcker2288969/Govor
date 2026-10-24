using Govor.Application.PrivateUserChats;
using Govor.Application.Groups;
using Govor.Application.Infrastructure.AdminsStuff;
using Govor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Application.Tests.PrivateUserChats;

[TestFixture]
public class UserPrivateChatsCreatorTests
{
    private TestDatabase _db = null!;
    private Mock<IPrivateChatGroupManager> _manager = null!;
    private UserPrivateChatsCreator _service = null!;
    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        _manager = new Mock<IPrivateChatGroupManager>();
        _service = new(_db.Context, _manager.Object, NullLogger<UserPrivateChatsCreator>.Instance);
    }
    [TearDown] public void TearDown() => _db.Dispose();

    [Test]
    public async Task ExistingChatIsFoundInEitherUserOrder()
    {
        Assert.That((await _service.CreateAsync(_db.Alice, _db.Bob)).Id, Is.EqualTo(_db.ChatId));
        Assert.That((await _service.CreateAsync(_db.Bob, _db.Alice)).Id, Is.EqualTo(_db.ChatId));
        Assert.That(await _db.Context.PrivateChats.CountAsync(), Is.EqualTo(1));
        _manager.VerifyNoOtherCalls();
    }

    [Test]
    public async Task NewChatIsSavedAndUsersJoinSignalRGroupOnce()
    {
        var chat = await _service.CreateAsync(_db.Alice, _db.Outsider);
        Assert.That(await _db.Context.PrivateChats.AnyAsync(c => c.Id == chat.Id), Is.True);
        await _service.CreateAsync(_db.Outsider, _db.Alice);
        _manager.Verify(m => m.AddUsersToPrivateChatGroupAsync(It.Is<PrivateChat>(c => c.Id == chat.Id)), Times.Once);
    }

    [Test]
    public async Task GroupListExcludesBannedAndUnrelatedMemberships()
    {
        var group = new ChatGroup { Id = Guid.NewGuid(), Name = "group", Description = "" };
        _db.Context.ChatGroups.Add(group);
        _db.Context.GroupMemberships.AddRange(
            new GroupMembership { Id = Guid.NewGuid(), GroupId = group.Id, UserId = _db.Alice },
            new GroupMembership { Id = Guid.NewGuid(), GroupId = group.Id, UserId = _db.Bob, IsBanned = true });
        await _db.Context.SaveChangesAsync();
        var getter = new UserGroupsGetterService(_db.Context);
        Assert.That((await getter.GetUserGroupsAsync(_db.Alice)).Select(g => g.Id), Is.EqualTo(new[] { group.Id }));
        Assert.That(await getter.GetUserGroupsAsync(_db.Bob), Is.Empty);
        Assert.That(await getter.GetUserGroupsAsync(_db.Outsider), Is.Empty);
    }

    [Test]
    public async Task InvitationGeneratorPersistsUniqueCodesAndMetadata()
    {
        var generator = new InvitationGenerator(_db.Context);
        var end = DateTime.UtcNow.Date.AddDays(2);
        var first = await generator.GenerateInvitationCode(end, 5, true, "admin");
        var second = await generator.GenerateInvitationCode(end, 10, false);
        Assert.That(first, Is.Not.EqualTo(second));
        var invite = await _db.Context.Invitations.AsNoTracking().SingleAsync(i => i.Code == first);
        Assert.That(invite.EndDate, Is.EqualTo(end));
        Assert.That(invite.MaxParticipants, Is.EqualTo(5));
        Assert.That(invite.IsAdmin, Is.True);
        Assert.That(invite.Description, Is.EqualTo("admin"));
    }
}
