using Govor.API.Common.SignalR.Helpers;
using Govor.API.Hubs;
using Govor.Application.Friends;
using Govor.Application.Medias;
using Govor.Application.Profiles;
using Govor.Application.Synching;
using Govor.Application.Users.UserOnlineStatus;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain.Common;
using Govor.Domain.Models.Users;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartRes;

namespace Govor.API.Tests.Unit.Hubs;

[TestFixture]
public class ProfileHubTests
{
    private readonly Guid _user = Guid.NewGuid(), _friend = Guid.NewGuid();
    private Mock<IFriendshipService> _friends = null!;
    private Mock<IProfileService> _profiles = null!;
    private Mock<IMediaService> _media = null!;
    private Mock<IClientProxy> _proxy = null!;
    private Mock<IHubCallerClients> _clients = null!;
    private Mock<IGroupManager> _groups = null!;
    private ProfileHub _hub = null!;
    private List<string> _recipients = null!;

    [SetUp]
    public void Setup()
    {
        _friends = new(); _profiles = new(); _media = new(); _proxy = new(); _clients = new(); _groups = new(); _recipients = [];
        var accessor = new Mock<IHubUserAccessor>(); accessor.Setup(x => x.GetUserId(It.IsAny<HubCallerContext>(), It.IsAny<bool>())).Returns(_user);
        var sync = new Mock<ISynchingService>(); sync.Setup(x => x.NormalizeNewlines(It.IsAny<string>())).Returns((string s) => s.Replace("\r\n", "\n"));
        _clients.Setup(x => x.Groups(It.IsAny<IReadOnlyList<string>>())).Callback<IReadOnlyList<string>>(ids => _recipients.AddRange(ids)).Returns(_proxy.Object);
        var context = new Mock<HubCallerContext>(); context.SetupGet(x => x.ConnectionId).Returns("connection");
        _friends.Setup(x => x.GetFriendsAsync(_user)).ReturnsAsync([new() {Id = _friend}, new() {Id = Guid.Empty}]);
        _friends.Setup(x => x.GetPotentialFriendsAsync(_user)).ReturnsAsync([new() {Id = _friend}, new() {Id = Guid.Empty}]);
        _profiles.Setup(x => x.SetDescription(It.IsAny<string>(), _user)).ReturnsAsync(SmartRes.Unit.Value);
        _profiles.Setup(x => x.SetNewIcon(_user, It.IsAny<Guid>())).ReturnsAsync(SmartRes.Unit.Value);
        _media.Setup(x => x.HasMediaAsync(It.IsAny<Guid>())).ReturnsAsync(true);
        _hub = new(_friends.Object, _profiles.Object, accessor.Object, sync.Object, _media.Object, NullLogger<ProfileHub>.Instance) { Clients = _clients.Object, Context = context.Object, Groups = _groups.Object };
    }
    [TearDown] public void TearDown() => _hub.Dispose();

    [TestCase(null)] [TestCase("")]
    public async Task DescriptionNullIsRejectedAndEmptyIsAllowed(string? description)
    {
        var result = await _hub.SetDescription(description);
        Assert.That(result.Status, Is.EqualTo(description is null ? HubResultStatus.BadRequest : HubResultStatus.Success));
        if (description is null) _profiles.VerifyNoOtherCalls();
    }
    [TestCase(500, HubResultStatus.Success)] [TestCase(501, HubResultStatus.BadRequest)]
    public async Task DescriptionLengthBoundary(int size, HubResultStatus expected) => Assert.That((await _hub.SetDescription(new string('a',size))).Status, Is.EqualTo(expected));

    [Test]
    public async Task DescriptionNormalizesAndNotifiesDistinctNonemptyRecipients()
    {
        var result = await _hub.SetDescription("one\r\ntwo");
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.Success));
        _profiles.Verify(x => x.SetDescription("one\ntwo", _user), Times.Once);
        Assert.That(_recipients, Is.EquivalentTo(new[] {$"user:{_user}", $"user:{_friend}"}));
        _proxy.Verify(x => x.SendCoreAsync("DescriptionUpdated", It.Is<object?[]>(args => args.Length == 1), default), Times.Once);
    }

    [TestCase(false)] [TestCase(true)]
    public async Task ProfileServiceFailuresDoNotNotify(bool avatar)
    {
        _profiles.Setup(x => x.SetDescription(It.IsAny<string>(), _user)).ReturnsAsync(Result.Failure<SmartRes.Unit>(Error.Forbidden("denied", "denied")));
        _profiles.Setup(x => x.SetNewIcon(_user, It.IsAny<Guid>())).ReturnsAsync(Result.Failure<SmartRes.Unit>(Error.Forbidden("denied", "denied")));
        var result = avatar ? await _hub.SetAvatar(Guid.NewGuid()) : await _hub.SetDescription("hello");
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.Forbidden)); _proxy.VerifyNoOtherCalls();
    }

    [TestCase(false)] [TestCase(true)]
    public async Task ProfileServiceExceptionsReturnServerError(bool avatar)
    {
        _profiles.Setup(x => x.SetDescription(It.IsAny<string>(), _user)).ThrowsAsync(new Exception("failure"));
        _profiles.Setup(x => x.SetNewIcon(_user, It.IsAny<Guid>())).ThrowsAsync(new Exception("failure"));
        var result = avatar ? await _hub.SetAvatar(Guid.NewGuid()) : await _hub.SetDescription("hello");
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.ServerError)); _proxy.VerifyNoOtherCalls();
    }

    [TestCase(true)] [TestCase(false)]
    public async Task InvalidAvatarNeverChangesProfile(bool empty)
    {
        _media.Setup(x => x.HasMediaAsync(It.IsAny<Guid>())).ReturnsAsync(false);
        Assert.That((await _hub.SetAvatar(empty ? Guid.Empty : Guid.NewGuid())).Status, Is.EqualTo(HubResultStatus.BadRequest));
        _profiles.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AvatarPersistsAndNotifies()
    {
        var icon = Guid.NewGuid();
        Assert.That((await _hub.SetAvatar(icon)).Status, Is.EqualTo(HubResultStatus.Success));
        _profiles.Verify(x => x.SetNewIcon(_user, icon), Times.Once);
        _proxy.Verify(x => x.SendCoreAsync("AvatarUpdated", It.IsAny<object?[]>(), default), Times.Once);
    }

    [TestCase(false)] [TestCase(true)]
    public async Task NotificationFailuresDoNotUndoPersistedProfile(bool deliveryFailure)
    {
        if (deliveryFailure) _proxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), default)).ThrowsAsync(new Exception("transport"));
        else {
            _friends.Setup(x => x.GetFriendsAsync(_user)).ThrowsAsync(new Exception("friends"));
            _friends.Setup(x => x.GetPotentialFriendsAsync(_user)).ThrowsAsync(new Exception("potential"));
        }
        Assert.That((await _hub.SetDescription("hello")).Status, Is.EqualTo(HubResultStatus.Success));
        if (!deliveryFailure) Assert.That(_recipients, Is.EqualTo(new[] { $"user:{_user}" }));
    }

    [TestCase(false)] [TestCase(true)]
    public async Task LifecycleMaintainsUserGroup(bool error)
    {
        await _hub.OnConnectedAsync(); await _hub.OnDisconnectedAsync(error ? new Exception("disconnect") : null);
        _groups.Verify(x => x.AddToGroupAsync("connection", $"user:{_user}", default), Times.Once);
        _groups.Verify(x => x.RemoveFromGroupAsync("connection", $"user:{_user}", default), Times.Once);
    }
}

[TestFixture]
public class PresenceHubTests
{
    [TestCase(true, true)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(false, false)]
    public async Task OnlyFirstConnectionAndLastDisconnectionBroadcast(bool first, bool last)
    {
        var user = Guid.NewGuid(); var friend = Guid.NewGuid();
        var accessor = new Mock<IHubUserAccessor>(); accessor.Setup(x => x.GetUserId(It.IsAny<HubCallerContext>(), It.IsAny<bool>())).Returns(user);
        var store = new Mock<IOnlineUserStore>(); store.Setup(x => x.AddConnection(user,"connection")).Returns(first); store.Setup(x => x.RemoveConnection(user,"connection")).Returns(last);
        var scope = new Mock<IUserNotificationScopeService>(); scope.Setup(x => x.GetNotifiedUsers(user)).ReturnsAsync([friend]);
        var proxy = new Mock<IClientProxy>(); var clients = new Mock<IHubCallerClients>(); clients.Setup(x => x.Groups(It.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] {friend.ToString()})))).Returns(proxy.Object);
        var groups = new Mock<IGroupManager>(); var context = new Mock<HubCallerContext>(); context.SetupGet(x => x.ConnectionId).Returns("connection");
        using var hub = new PresenceHub(NullLogger<PresenceHub>.Instance,scope.Object,store.Object,accessor.Object) { Context = context.Object, Clients = clients.Object, Groups = groups.Object };
        await hub.OnConnectedAsync(); await hub.OnDisconnectedAsync(null!);
        proxy.Verify(x => x.SendCoreAsync("UserOnline",It.Is<object?[]>(args => Equals(args[0],user)),default),first ? Times.Once() : Times.Never());
        proxy.Verify(x => x.SendCoreAsync("UserOffline",It.Is<object?[]>(args => Equals(args[0],user)),default),last ? Times.Once() : Times.Never());
        groups.Verify(x => x.AddToGroupAsync("connection",user.ToString(),default),Times.Once);
        groups.Verify(x => x.RemoveFromGroupAsync("connection",user.ToString(),default),Times.Once);
    }
    [Test]
    public async Task InvalidUserAbortsWithoutStoreOrNotifications()
    {
        var accessor = new Mock<IHubUserAccessor>(); var context = new Mock<HubCallerContext>();
        var store = new Mock<IOnlineUserStore>(MockBehavior.Strict); var scope = new Mock<IUserNotificationScopeService>(MockBehavior.Strict);
        using var hub = new PresenceHub(NullLogger<PresenceHub>.Instance,scope.Object,store.Object,accessor.Object) {Context=context.Object};
        await hub.OnConnectedAsync(); await hub.OnDisconnectedAsync(null!);
        context.Verify(x => x.Abort(),Times.Once); store.VerifyNoOtherCalls(); scope.VerifyNoOtherCalls();
    }
}

