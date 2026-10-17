using Govor.API.Hubs;
using Govor.API.Hubs.Infrastructure;
using Govor.Application.PrivateUserChats;
using Govor.Application.Profiles;
using Govor.Application.PushNotifications;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models.Users;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartRes;

namespace Govor.Server.Tests;

[TestFixture]
public class NotificationTests
{
    private TestDatabase _db = null!;
    private Mock<IPushNotificationService> _push = null!;
    private Mock<IClientProxy> _proxy = null!;
    private ChatNotificationService _notifier = null!;
    private readonly List<(string Method, string[] Groups, object Payload)> _events = [];
    private string[] _targetGroups = [];
    private Guid _aliceSession;
    private Guid _bobSession;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        _events.Clear();
        _aliceSession = AddSession(_db.Alice);
        _bobSession = AddSession(_db.Bob);
        AddSession(_db.Bob, revoked: true);
        AddSession(_db.Outsider);
        _proxy = new Mock<IClientProxy>();
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((method, args, _) => _events.Add((method, _targetGroups, args[0]!)))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Groups(It.IsAny<IReadOnlyList<string>>()))
            .Callback<IReadOnlyList<string>>(groups => _targetGroups = groups.ToArray())
            .Returns(_proxy.Object);
        var hub = new Mock<IHubContext<ChatsHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        var chats = new Mock<IUserPrivateChatsGetterService>();
        chats.Setup(c => c.GetPrivateChatAsync(_db.ChatId))
            .ReturnsAsync(Result<PrivateChat, Error>.Success(_db.Context.PrivateChats.Single()));
        var profiles = new Mock<IProfileService>();
        profiles.Setup(p => p.GetUserProfileAsync(_db.Alice))
            .ReturnsAsync(Result<UserProfile, Error>.Success(new UserProfile { Id = _db.Alice, Username = "Alice" }));
        _push = new Mock<IPushNotificationService>();
        _push.Setup(p => p.SendToUsersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
            .ReturnsAsync(Govor.Domain.Common.Result.Success());
        _notifier = new ChatNotificationService(hub.Object, _push.Object, chats.Object, profiles.Object,
            _db.Context, NullLogger<ChatNotificationService>.Instance);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task PrivateMessageReachesBothActiveSessionsAndPushGoesOnlyToRecipient()
    {
        var message = Message();
        await _notifier.NotifyMessageSentAsync(message);
        await _notifier.DeliverPushAsync(message);
        Assert.That(_events.Single(e => e.Method == ChatHubConstants.ReceiveMessage).Groups,
            Is.EquivalentTo(new[] { ChatHubConstants.GetSessionGroup(_aliceSession), ChatHubConstants.GetSessionGroup(_bobSession) }));
        Assert.That(_events.Single(e => e.Method == ChatHubConstants.MessageSent).Groups,
            Is.EqualTo(new[] { ChatHubConstants.GetSessionGroup(_aliceSession) }));
        _push.Verify(p => p.SendToUsersAsync(It.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { _db.Bob })),
            "Alice", "New message", "chat_messages", $"chat_{_db.ChatId}",
            It.Is<Dictionary<string, string>?>(data => data != null && data["messageId"] == message.MessageId.ToString())), Times.Once);
    }

    [Test]
    public async Task GroupMessageExcludesBannedMembersFromSignalRAndPush()
    {
        var group = new ChatGroup { Id = Guid.NewGuid(), Name = "group", Description = "" };
        _db.Context.ChatGroups.Add(group);
        foreach (var user in new[] { _db.Alice, _db.Bob, _db.Outsider })
            _db.Context.GroupMemberships.Add(new GroupMembership
            { Id = Guid.NewGuid(), GroupId = group.Id, UserId = user, IsBanned = user == _db.Outsider });
        _db.Context.SaveChanges();
        var message = Message();
        message.RecipientId = group.Id;
        message.RecipientType = RecipientType.Group;
        await _notifier.NotifyMessageSentAsync(message);
        await _notifier.DeliverPushAsync(message);
        Assert.That(_events.Single(e => e.Method == ChatHubConstants.ReceiveMessage).Groups.Length, Is.EqualTo(2));
        _push.Verify(p => p.SendToUsersAsync(It.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { _db.Bob })),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>?>()), Times.Once);
    }

    [Test]
    public async Task MessageReadBeforeRetrySuppressesPush()
    {
        var saved = _db.AddMessage();
        _db.Context.MessageViews.Add(new MessageView
        { Id = Guid.NewGuid(), MessageId = saved.Id, UserId = _db.Bob, ViewedAt = DateTime.UtcNow });
        await _db.Context.SaveChangesAsync();
        var message = Message();
        message.MessageId = saved.Id;

        Assert.That(await _notifier.DeliverPushAsync(message), Is.True);
        _push.Verify(p => p.SendToUsersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()), Times.Never);
    }

    [Test]
    public async Task PushDoesNotExposeContentAndKeepsRoutingData()
    {
        var message = Message();
        message.EncryptedContent = "sensitive plaintext or ciphertext";
        Assert.That(await _notifier.DeliverPushAsync(message), Is.True);
        _push.Verify(p => p.SendToUsersAsync(It.IsAny<IEnumerable<Guid>>(), "Alice", "New message",
            "chat_messages", $"chat_{_db.ChatId}", It.Is<Dictionary<string, string>?>(data => data != null &&
                data["type"] == "chat_message" && data["senderId"] == _db.Alice.ToString() &&
                data["chatId"] == _db.ChatId.ToString() && data["isGroup"] == "false" &&
                data["messageId"] == message.MessageId.ToString() && !data.Values.Contains(message.EncryptedContent))), Times.Once);
    }

    [Test]
    public async Task SignalRFailureStillAttemptsPushAndProviderFailureDoesNotFailSavedSend()
    {
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("offline"));
        _push.Setup(p => p.SendToUsersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));
        await _notifier.NotifyMessageSentAsync(Message());
        Assert.That(await _notifier.DeliverPushAsync(Message()), Is.False);
        _push.Verify(p => p.SendToUsersAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()), Times.Once);
    }

    [Test]
    public async Task ReadNotificationReachesReaderAndAuthorDevices()
    {
        await _notifier.NotifyChatReadAsync(new ChatReadResponse { ChatId = _db.ChatId, ReaderId = _db.Bob, RecipientType = RecipientType.User });
        Assert.That(_events.Single().Method, Is.EqualTo(ChatHubConstants.ChatRead));
        Assert.That(_events.Single().Groups, Is.EquivalentTo(new[]
        { ChatHubConstants.GetSessionGroup(_aliceSession), ChatHubConstants.GetSessionGroup(_bobSession) }));
    }

    [Test]
    public async Task ReactionChangesReachBothParticipantsActiveDevices()
    {
        await _notifier.NotifyMessageReactionsChangedAsync(new MessageReactionsChangedResponse
        { MessageId = Guid.NewGuid(), RecipientId = _db.ChatId, RecipientType = RecipientType.User, ActorId = _db.Bob });
        Assert.That(_events.Single().Method, Is.EqualTo(ChatHubConstants.MessageReactionsChanged));
        Assert.That(_events.Single().Groups, Is.EquivalentTo(new[]
        { ChatHubConstants.GetSessionGroup(_aliceSession), ChatHubConstants.GetSessionGroup(_bobSession) }));
    }

    [Test]
    public async Task ChannelPolicyChangesExcludeBannedMembers()
    {
        var group = new ChatGroup { Id = Guid.NewGuid(), Name = "channel", Description = "", IsChannel = true };
        _db.Context.ChatGroups.Add(group);
        foreach (var user in new[] { _db.Alice, _db.Bob, _db.Outsider })
            _db.Context.GroupMemberships.Add(new GroupMembership
            { Id = Guid.NewGuid(), GroupId = group.Id, UserId = user, IsBanned = user == _db.Outsider });
        await _db.Context.SaveChangesAsync();
        await _notifier.NotifyChannelReactionPolicyChangedAsync(new ChannelReactionPolicyResponse { GroupId = group.Id });
        Assert.That(_events.Single().Method, Is.EqualTo(ChatHubConstants.ChannelReactionPolicyChanged));
        Assert.That(_events.Single().Groups, Is.EquivalentTo(new[]
        { ChatHubConstants.GetSessionGroup(_aliceSession), ChatHubConstants.GetSessionGroup(_bobSession) }));
    }

    [Test]
    public async Task GroupProfileChangeReachesAllActiveDevicesWithoutLeakingToBannedOrForeignUsers()
    {
        var group = AddGroup();
        var secondDevice = AddSession(_db.Bob);
        var expired = AddSession(_db.Bob);
        await _db.Context.UserSessions.Where(s => s.Id == expired)
            .ExecuteUpdateAsync(s => s.SetProperty(s => s.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        await _notifier.NotifyGroupProfileChangedAsync(group.Id);
        Assert.That(_events.Single().Method, Is.EqualTo(ChatHubConstants.GroupProfileChanged));
        Assert.That(_events.Single().Groups, Is.EquivalentTo(new[]
        {
            ChatHubConstants.GetSessionGroup(_aliceSession), ChatHubConstants.GetSessionGroup(_bobSession),
            ChatHubConstants.GetSessionGroup(secondDevice)
        }));
        Assert.That(((GroupProfileChangedResponse)_events.Single().Payload).GroupId, Is.EqualTo(group.Id));
    }

    [TestCase(GroupMemberStatus.Active, GroupRole.Member)]
    [TestCase(GroupMemberStatus.Active, GroupRole.Admin)]
    [TestCase(GroupMemberStatus.Active, GroupRole.Owner)]
    [TestCase(GroupMemberStatus.Banned, null)]
    [TestCase(GroupMemberStatus.Left, null)]
    public async Task MembershipEventReportsCommittedStatusAndStillReachesAffectedUser(GroupMemberStatus status, GroupRole? role)
    {
        var group = AddGroup();
        if (status == GroupMemberStatus.Banned)
            await _db.Context.GroupMemberships.Where(m => m.GroupId == group.Id && m.UserId == _db.Bob)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsBanned, true));
        if (status == GroupMemberStatus.Left)
            await _db.Context.GroupMemberships.Where(m => m.GroupId == group.Id && m.UserId == _db.Bob).ExecuteDeleteAsync();
        if (role == GroupRole.Admin)
        {
            _db.Context.GroupAdmins.Add(new GroupAdmins { Id = Guid.NewGuid(), GroupId = group.Id, UserId = _db.Bob });
            await _db.Context.SaveChangesAsync();
        }
        if (role == GroupRole.Owner)
            await _db.Context.ChatGroups.Where(g => g.Id == group.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.OwnerUserId, _db.Bob));
        await _notifier.NotifyGroupMemberChangedAsync(group.Id, _db.Bob);
        var change = (GroupMemberChangedResponse)_events.Single().Payload;
        Assert.That(change.GroupId, Is.EqualTo(group.Id));
        Assert.That(change.UserId, Is.EqualTo(_db.Bob));
        Assert.That(change.Status, Is.EqualTo(status));
        Assert.That(change.Role, Is.EqualTo(role));
        Assert.That(_events.Single().Groups, Is.EquivalentTo(new[]
        { ChatHubConstants.GetSessionGroup(_aliceSession), ChatHubConstants.GetSessionGroup(_bobSession) }));
        _events.Clear();
        await _notifier.NotifyGroupProfileChangedAsync(group.Id);
        Assert.That(_events.Single().Groups.Contains(ChatHubConstants.GetSessionGroup(_bobSession)),
            Is.EqualTo(status == GroupMemberStatus.Active));
    }

    [Test]
    public async Task GroupEventDeliveryFailureDoesNotFailTheAlreadyCommittedOperation()
    {
        var group = AddGroup();
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("offline"));
        await _notifier.NotifyGroupProfileChangedAsync(group.Id);
        await _notifier.NotifyGroupMemberChangedAsync(group.Id, _db.Bob);
        await _notifier.NotifyUserJoinedGroupsAsync(_db.Bob);
    }

    private ChatGroup AddGroup()
    {
        var group = new ChatGroup { Id = Guid.NewGuid(), Name = "Private", Description = "", IsPrivate = true, OwnerUserId = _db.Alice };
        _db.Context.ChatGroups.Add(group);
        foreach (var userId in new[] { _db.Alice, _db.Bob, _db.Outsider })
            _db.Context.GroupMemberships.Add(new GroupMembership
            { Id = Guid.NewGuid(), GroupId = group.Id, UserId = userId, IsBanned = userId == _db.Outsider });
        _db.Context.SaveChanges();
        return group;
    }

    private Guid AddSession(Guid userId, bool revoked = false)
    {
        var session = new UserSession { UserId = userId, RefreshTokenHash = Guid.NewGuid().ToString(), IsRevoked = revoked, ExpiresAt = DateTime.UtcNow.AddDays(1) };
        _db.Context.UserSessions.Add(session);
        _db.Context.SaveChanges();
        return session.Id;
    }

    private UserMessageResponse Message() => new()
    {
        MessageId = Guid.NewGuid(), SenderId = _db.Alice, RecipientId = _db.ChatId,
        RecipientType = RecipientType.User, EncryptedContent = "hello"
    };
}
