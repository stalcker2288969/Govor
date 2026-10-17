using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Govor.API.Hubs.Infrastructure;
using Govor.API.Hubs;
using Govor.Application.Storage;
using Govor.Contracts.Requests;
using Govor.Contracts.Responses;
using Govor.Domain.Models.Reactions;
using Microsoft.AspNetCore.SignalR;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain;
using Govor.Domain.Models.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace Govor.Server.Tests;

[TestFixture]
[NonParallelizable]
public class ReadApiTests
{
    private const string Secret = "Govor-server-tests-only-signing-key-at-least-64-characters-long-123456";
    private TestDatabase _db = null!;
    private TestFactory _factory = null!;
    private HttpClient _client = null!;
    private Mock<IChatNotificationService> _notifier = null!;
    private Guid _session;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        var session = new UserSession { UserId = _db.Bob, ExpiresAt = DateTime.UtcNow.AddDays(1) };
        _db.Context.UserSessions.Add(session);
        _db.Context.SaveChanges();
        _session = session.Id;
        _notifier = new Mock<IChatNotificationService>();
        _notifier.Setup(n => n.NotifyChatReadAsync(It.IsAny<ChatReadResponse>())).Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyMessageReactionsChangedAsync(It.IsAny<MessageReactionsChangedResponse>())).Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyChannelReactionPolicyChangedAsync(It.IsAny<ChannelReactionPolicyResponse>())).Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyGroupProfileChangedAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyGroupMemberChangedAsync(It.IsAny<Guid>(), It.IsAny<Guid>())).Returns(Task.CompletedTask);
        _notifier.Setup(n => n.NotifyUserJoinedGroupsAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        _factory = new TestFactory(_db, _notifier.Object);
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        _db.Dispose();
    }

    [Test]
    public async Task ReadEndpointPersistsReceiptsNotifiesAndReturnsUnreadCount()
    {
        var message = _db.AddMessage();
        Authorize();
        var response = await _client.PostAsJsonAsync($"/api/chats/{_db.ChatId}/read",
            new { recipientType = 0, messageIds = new[] { message.Id } });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ChatReadResponse>();
        Assert.That(body!.ReadCount, Is.EqualTo(1));
        Assert.That(body.UnreadCount, Is.Zero);
        _notifier.Verify(n => n.NotifyChatReadAsync(It.Is<ChatReadResponse>(r => r.ReaderId == _db.Bob && r.ChatId == _db.ChatId)), Times.Once);
        var count = await _client.GetAsync($"/api/chats/{_db.ChatId}/unread-count?recipientType=0");
        Assert.That(count.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await count.Content.ReadAsStringAsync(), Does.Contain("\"unreadCount\":0"));
    }

    [Test]
    public async Task PushRegistrationRotationAndUnregisterAffectOnlyTheAuthenticatedSession()
    {
        var otherSession = new UserSession { UserId = _db.Bob, ExpiresAt = DateTime.UtcNow.AddDays(1), RefreshTokenHash = "other-device" };
        _db.Context.UserSessions.Add(otherSession);
        _db.Context.UserPushTokens.Add(new UserPushToken
        { UserId = _db.Bob, UserSessionId = otherSession.Id, Token = "other-device", Platform = "android" });
        await _db.Context.SaveChangesAsync();
        Assert.That((await _client.DeleteAsync("/api/pushes/token")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize();
        foreach (var token in new[] { "original", "rotated" })
            Assert.That((await _client.PostAsJsonAsync("/api/pushes/token/register", new { token, platform = "android" })).StatusCode,
                Is.EqualTo(HttpStatusCode.NoContent));
        var registered = await _db.Context.UserPushTokens.AsNoTracking().SingleAsync(t => t.UserSessionId == _session);
        Assert.That(registered.Token, Is.EqualTo("rotated"));
        foreach (var _ in Enumerable.Range(0, 2))
            Assert.That((await _client.DeleteAsync("/api/pushes/token")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _db.Context.UserPushTokens.AsNoTracking().SingleAsync(t => t.UserSessionId == _session)).IsActive, Is.False);
        Assert.That((await _db.Context.UserPushTokens.AsNoTracking().SingleAsync(t => t.UserSessionId == otherSession.Id)).IsActive, Is.True);
        Assert.That((await _client.PostAsJsonAsync("/api/pushes/token/register", new { token = "rotated", platform = "android" })).StatusCode,
            Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _db.Context.UserPushTokens.AsNoTracking().SingleAsync(t => t.UserSessionId == _session)).IsActive, Is.True);
    }

    [TestCase(null, "android")]
    [TestCase("", "android")]
    [TestCase("   ", "android")]
    [TestCase("token", " ")]
    [TestCase("token", null)]
    public async Task PushRegistrationRejectsMissingOrBlankFields(string? token, string? platform)
    {
        Authorize();
        Assert.That((await _client.PostAsJsonAsync("/api/pushes/token/register", new { token, platform })).StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await _db.Context.UserPushTokens.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task AnonymousAndRefreshTokenCannotUseReadApi()
    {
        var path = $"/api/chats/{_db.ChatId}/unread-count?recipientType=0";
        Assert.That((await _client.GetAsync(path)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize(tokenType: "refresh");
        Assert.That((await _client.GetAsync(path)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task SessionRevocationInvalidatesAnAlreadyIssuedAccessToken()
    {
        Authorize();
        await _db.Context.UserSessions.Where(s => s.Id == _session)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.IsRevoked, true));
        var response = await _client.GetAsync($"/api/chats/{_db.ChatId}/unread-count?recipientType=0");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ForeignChatIsForbiddenAndInvalidSelectionIsBadRequest()
    {
        var message = _db.AddMessage();
        Authorize();
        var foreign = await _client.PostAsJsonAsync($"/api/chats/{Guid.NewGuid()}/read", new { recipientType = 0 });
        Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var invalid = await _client.PostAsJsonAsync($"/api/chats/{_db.ChatId}/read",
            new { recipientType = 0, messageIds = new[] { message.Id }, upToMessageId = message.Id });
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await _db.Context.MessageViews.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task OrdinaryUserCannotReadAdminUserList()
    {
        Authorize();
        var response = await _client.GetAsync("/api/admin/Users/all");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task ReactionHttpApiReturnsVersionedCountsAndNotifiesOnlyOnChange()
    {
        var message = _db.AddMessage();
        Authorize();
        var packs = await _client.GetFromJsonAsync<List<ReactionPackResponse>>("/api/reaction-packs/mine");
        var reaction = packs!.Single(p => p.IsDefault).Reactions.First().Id;
        var path = $"/api/messages/{message.Id}/reactions";
        var response = await _client.PutAsJsonAsync(path + "/me", new { reactionId = reaction });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var state = await response.Content.ReadFromJsonAsync<MessageReactionsChangedResponse>();
        Assert.That(state!.Version, Is.EqualTo(1));
        Assert.That(state.ActorReaction!.ReactionId, Is.EqualTo(reaction));
        Assert.That(state.Counts.Single().Count, Is.EqualTo(1));
        Assert.That((await _client.PutAsJsonAsync(path + "/me", new { reactionId = reaction })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _notifier.Verify(n => n.NotifyMessageReactionsChangedAsync(It.IsAny<MessageReactionsChangedResponse>()), Times.Once);
        Assert.That((await _client.PutAsJsonAsync(path + "/me", new { reactionId = Guid.Empty })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var removed = await _client.DeleteAsync(path + "/me");
        Assert.That(removed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var empty = await _client.GetFromJsonAsync<MessageReactionsChangedResponse>(path);
        Assert.That(empty!.Version, Is.EqualTo(2));
        Assert.That(empty.Counts, Is.Empty);
    }

    [Test]
    public async Task AdminPackUploadUsesRealContentAndCanBeSharedAndDownloaded()
    {
        Authorize();
        Assert.That((await _client.PostAsJsonAsync("/api/admin/reaction-packs", new { name = "Custom" })).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
        Authorize(role: "Admin");
        Assert.That((await _client.PostAsJsonAsync("/api/admin/reaction-packs", new { name = "Custom" })).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
        ReactionTests.MakeAdmin(_db.Context, _db.Bob);
        var created = await _client.PostAsJsonAsync("/api/admin/reaction-packs", new { name = "Custom", description = "Shared" });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var pack = (await created.Content.ReadFromJsonAsync<ReactionPackResponse>())!;
        using var image = new Image<Rgba32>(256, 256);
        using var bytes = new MemoryStream();
        await image.SaveAsPngAsync(bytes);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("Picture"), "name");
        var upload = new ByteArrayContent(bytes.ToArray());
        upload.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        multipart.Add(upload, "file", "incorrect.txt");
        var response = await _client.PostAsync($"/api/admin/reaction-packs/{pack.Id}/reactions/media", multipart);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var item = (await response.Content.ReadFromJsonAsync<ReactionItemResponse>())!;
        Assert.That(item.Kind, Is.EqualTo(ReactionKind.Image));
        Assert.That(item.Width, Is.EqualTo(128));
        Authorize();
        var shared = await _client.GetFromJsonAsync<ReactionPackResponse>(pack.SharePath);
        Assert.That(shared!.Reactions.Single().Id, Is.EqualTo(item.Id));
        Assert.That((await _client.PutAsync($"/api/reaction-packs/{pack.Id}/subscription", null)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        var downloaded = await _client.GetAsync(item.MediaUrl);
        Assert.That(downloaded.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(downloaded.Content.Headers.ContentType!.MediaType, Is.EqualTo("image/png"));
        using var decoded = Image.Load(await downloaded.Content.ReadAsByteArrayAsync());
        Assert.That(decoded.Width, Is.EqualTo(128));
    }

    [Test]
    public async Task SignalRReactionMethodsUseTheSameStateAndAccessChecks()
    {
        var message = _db.AddMessage();
        using var scope = _factory.Services.CreateScope();
        var hub = ActivatorUtilities.CreateInstance<ChatsHub>(scope.ServiceProvider);
        var caller = new Mock<HubCallerContext>();
        caller.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(new[]
        { new Claim("userId", _db.Bob.ToString()), new Claim("sid", _session.ToString()) }, "test")));
        hub.Context = caller.Object;
        var reaction = DefaultReactionPack.Items().First().Id;
        var result = await hub.React(message.Id, new SetReactionRequest { ReactionId = reaction });
        Assert.That(result.Status, Is.EqualTo(HubResultStatus.Success));
        Assert.That(result.Result!.Counts.Single().Count, Is.EqualTo(1));
        Assert.That((await hub.RemoveReaction(message.Id)).Status, Is.EqualTo(HubResultStatus.Success));
        Assert.That((await hub.React(Guid.NewGuid(), new SetReactionRequest { ReactionId = reaction })).Status, Is.EqualTo(HubResultStatus.NotFound));
    }

    [Test]
    public async Task PrivateGroupHttpInvitationPreviewJoinAndRolesRespectPermissions()
    {
        Authorize();
        var created = await _client.PostAsJsonAsync("/api/groups", new { name = "PrivateGroup", description = "test", isPrivate = true });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var group = (await created.Content.ReadFromJsonAsync<GroupResponse>())!;
        Assert.That(group.MyRole, Is.EqualTo(Govor.Domain.Models.GroupRole.Owner));
        Assert.That(group.MemberCount, Is.EqualTo(1));
        var invitation = await _client.PostAsJsonAsync($"/api/groups/{group.Id}/invitations", new { maxParticipants = 1 });
        var link = (await invitation.Content.ReadFromJsonAsync<GroupInvitationResponse>())!;
        Authorize(userId: _db.Alice);
        Assert.That((await _client.GetAsync($"/api/groups/{group.Id}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.GetFromJsonAsync<List<GroupResponse>>("/api/groups/search?q=PrivateGroup")), Is.Empty);
        Assert.That((await _client.GetAsync(link.PreviewPath)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await _db.Context.GroupMemberships.AnyAsync(m => m.GroupId == group.Id && m.UserId == _db.Alice), Is.False);
        Assert.That((await _client.PostAsync(link.JoinPath, null)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PutAsJsonAsync($"/api/groups/{group.Id}", new { name = "Hijacked" })).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await _client.PutAsync($"/api/groups/{group.Id}/members/{_db.Alice}/administrator", null)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Authorize();
        Assert.That((await _client.PutAsync($"/api/groups/{group.Id}/members/{_db.Alice}/administrator", null)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Authorize(userId: _db.Alice);
        Assert.That((await _client.PutAsync($"/api/groups/{group.Id}/members/{_db.Bob}/ban", null)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await _client.DeleteAsync($"/api/groups/{group.Id}/members/me")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _client.GetAsync($"/api/groups/{group.Id}/messages")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task ChannelOwnerControlsReactionsAndModerationHttpNotifiesDeletion()
    {
        Authorize();
        var created = await _client.PostAsJsonAsync("/api/groups", new { name = "Channel", isPrivate = false, isChannel = true });
        var group = (await created.Content.ReadFromJsonAsync<GroupResponse>())!;
        var id = group.Id;
        var policy = await _client.PutAsJsonAsync($"/api/groups/{id}/reactions", new
        { mode = 2, reactionIds = new[] { DefaultReactionPack.Items()[0].Id } });
        Assert.That(policy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _notifier.Verify(n => n.NotifyChannelReactionPolicyChangedAsync(It.Is<ChannelReactionPolicyResponse>(p => p.GroupId == id)), Times.Once);
        Authorize(userId: _db.Alice);
        await _client.PostAsync($"/api/groups/{id}/join", null);
        Assert.That((await _client.PutAsJsonAsync($"/api/groups/{id}/reactions", new { mode = 1 })).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Authorize();
        var own = _db.AddMessage(sender: _db.Bob, chat: id, type: Govor.Domain.Models.Messages.RecipientType.Group);
        Assert.That((await _client.DeleteAsync($"/api/groups/{Guid.NewGuid()}/messages/{own.Id}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.DeleteAsync($"/api/groups/{id}/messages/{own.Id}")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        _notifier.Verify(n => n.NotifyMessageRemovedAsync(It.Is<MessageRemovedResponse>(m => m.MessageId == own.Id && m.RecipientId == id)), Times.Once);
    }

    [Test]
    public async Task RequiredChannelHttpRequiresServerAdminAndRegistrationAddsMembership()
    {
        Authorize();
        var created = await _client.PostAsJsonAsync("/api/groups", new { name = "Required", isPrivate = true, isChannel = true });
        var id = (await created.Content.ReadFromJsonAsync<GroupResponse>())!.Id;
        Assert.That((await _client.PutAsJsonAsync("/api/admin/required-channel", new { channelId = id, allowLeave = false })).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Authorize(role: "Admin");
        Assert.That((await _client.PutAsJsonAsync("/api/admin/required-channel", new { channelId = id })).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        ReactionTests.MakeAdmin(_db.Context, _db.Bob);
        Assert.That((await _client.PutAsJsonAsync("/api/admin/required-channel", new { channelId = id, allowLeave = false })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var invite = _db.Context.Invitations.Single(i => i.Code == "test");
        invite.Code = Guid.NewGuid().ToString("N");
        await _db.Context.SaveChangesAsync();
        _client.DefaultRequestHeaders.Authorization = null;
        var registered = await _client.PostAsJsonAsync("/api/auth/register", new
        { name = "НовыйУчастник123", password = "Secure-test123!", inviteLink = invite.Code, deviceInfo = "test" });
        Assert.That(registered.StatusCode, Is.EqualTo(HttpStatusCode.OK), await registered.Content.ReadAsStringAsync());
        var user = await _db.Context.Users.AsNoTracking().SingleAsync(u => u.Username == "НовыйУчастник123");
        _notifier.Verify(n => n.NotifyUserJoinedGroupsAsync(user.Id), Times.Once);
        Assert.That(await _db.Context.GroupMemberships.AnyAsync(m => m.UserId == user.Id && m.GroupId == id && !m.IsBanned), Is.True);
        Authorize(userId: user.Id);
        Assert.That((await _client.DeleteAsync($"/api/groups/{id}/members/me")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task GroupAvatarIsOwnerControlledAndPrivateFilesAreProtected()
    {
        Authorize();
        var created = await _client.PostAsJsonAsync("/api/groups", new { name = "AvatarGroup", isPrivate = false });
        var group = (await created.Content.ReadFromJsonAsync<GroupResponse>())!;
        using var image = new Image<Rgba32>(256, 256);
        using var bytes = new MemoryStream();
        await image.SaveAsPngAsync(bytes);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes.ToArray()), "file", "avatar.png");
        Assert.That((await _client.PostAsync($"/api/groups/{group.Id}/avatar", form)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _notifier.Verify(n => n.NotifyGroupProfileChangedAsync(group.Id), Times.Once);
        var updated = (await _client.GetFromJsonAsync<GroupResponse>($"/api/groups/{group.Id}"))!;
        Assert.That(updated.ImageId, Is.Not.EqualTo(Guid.Empty));
        Authorize(userId: _db.Alice);
        Assert.That((await _client.GetAsync(updated.ImageUrl)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.DeleteAsync($"/api/groups/{group.Id}/avatar")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        _notifier.Verify(n => n.NotifyGroupProfileChangedAsync(group.Id), Times.Once);
        Authorize();
        await _client.PutAsJsonAsync($"/api/groups/{group.Id}", new { name = "AvatarGroup", isPrivate = true });
        var invitation = await _client.PostAsJsonAsync($"/api/groups/{group.Id}/invitations", new { });
        var link = (await invitation.Content.ReadFromJsonAsync<GroupInvitationResponse>())!;
        Authorize(userId: _db.Alice);
        Assert.That((await _client.GetAsync(updated.ImageUrl)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        await _client.PostAsync(link.JoinPath, null);
        Assert.That((await _client.GetAsync(updated.ImageUrl)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Authorize();
        await _client.PutAsync($"/api/groups/{group.Id}/members/{_db.Alice}/ban", null);
        Authorize(userId: _db.Alice);
        Assert.That((await _client.GetAsync(updated.ImageUrl)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Authorize();
        Assert.That((await _client.DeleteAsync($"/api/groups/{group.Id}/avatar")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        _notifier.Verify(n => n.NotifyGroupProfileChangedAsync(group.Id), Times.Exactly(3));
    }

    [Test]
    public async Task GroupCommandsNotifyAfterSuccessAndDeniedCommandsDoNotNotify()
    {
        Authorize();
        var created = await _client.PostAsJsonAsync("/api/groups", new { name = "Live", isPrivate = false });
        var id = (await created.Content.ReadFromJsonAsync<GroupResponse>())!.Id;
        _notifier.Verify(n => n.NotifyGroupMemberChangedAsync(id, _db.Bob), Times.Once);
        Authorize(userId: _db.Alice);
        Assert.That((await _client.PostAsync($"/api/groups/{id}/join", null)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _notifier.Verify(n => n.NotifyGroupMemberChangedAsync(id, _db.Alice), Times.Once);
        _notifier.Invocations.Clear();
        Assert.That((await _client.PutAsJsonAsync($"/api/groups/{id}", new { name = "Forbidden", isPrivate = false })).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await _client.PutAsync($"/api/groups/{id}/members/{_db.Bob}/administrator", null)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await _client.PutAsync($"/api/groups/{id}/members/{_db.Bob}/ban", null)).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(_notifier.Invocations, Is.Empty);
        Authorize();
        Assert.That((await _client.PutAsJsonAsync($"/api/groups/{id}", new { name = "Renamed", isPrivate = true })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _notifier.Verify(n => n.NotifyGroupProfileChangedAsync(id), Times.Once);
        Assert.That((await _client.PutAsync($"/api/groups/{id}/members/{_db.Alice}/administrator", null)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _client.DeleteAsync($"/api/groups/{id}/members/{_db.Alice}/administrator")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _client.PutAsync($"/api/groups/{id}/members/{_db.Alice}/ban", null)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That((await _client.DeleteAsync($"/api/groups/{id}/members/{_db.Alice}/ban")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        _notifier.Verify(n => n.NotifyGroupMemberChangedAsync(id, _db.Alice), Times.Exactly(4));
        _notifier.Invocations.Clear();
        Assert.That((await _client.PostAsync($"/api/groups/{id}/ownership/{_db.Alice}", null)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        _notifier.Verify(n => n.NotifyGroupProfileChangedAsync(id), Times.Once);
        _notifier.Verify(n => n.NotifyGroupMemberChangedAsync(id, _db.Bob), Times.Once);
        _notifier.Verify(n => n.NotifyGroupMemberChangedAsync(id, _db.Alice), Times.Once);
        Assert.That((await _client.DeleteAsync($"/api/groups/{id}/members/me")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        _notifier.Verify(n => n.NotifyGroupMemberChangedAsync(id, _db.Bob), Times.Exactly(2));
    }

    [Test]
    public async Task InvitationPreviewDoesNotNotifyAndSuccessfulJoinDoes()
    {
        Authorize();
        var created = await _client.PostAsJsonAsync("/api/groups", new { name = "Invite", isPrivate = true });
        var id = (await created.Content.ReadFromJsonAsync<GroupResponse>())!.Id;
        var invitation = await _client.PostAsJsonAsync($"/api/groups/{id}/invitations", new { });
        var link = (await invitation.Content.ReadFromJsonAsync<GroupInvitationResponse>())!;
        _notifier.Invocations.Clear();
        Authorize(userId: _db.Alice);
        Assert.That((await _client.GetAsync(link.PreviewPath)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsync("/api/group-invites/invalid/join", null)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(_notifier.Invocations, Is.Empty);
        Assert.That((await _client.PostAsync(link.JoinPath, null)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _notifier.Verify(n => n.NotifyGroupMemberChangedAsync(id, _db.Alice), Times.Once);
    }

    [Test]
    public async Task RequiredChannelChangeNotifiesOldAndNewChannelButFailedChangeDoesNot()
    {
        ReactionTests.MakeAdmin(_db.Context, _db.Bob);
        Authorize(role: "Admin");
        var first = await _client.PostAsJsonAsync("/api/groups", new { name = "First", isChannel = true });
        var second = await _client.PostAsJsonAsync("/api/groups", new { name = "Second", isChannel = true });
        var firstId = (await first.Content.ReadFromJsonAsync<GroupResponse>())!.Id;
        var secondId = (await second.Content.ReadFromJsonAsync<GroupResponse>())!.Id;
        _notifier.Invocations.Clear();
        Assert.That((await _client.PutAsJsonAsync("/api/admin/required-channel", new { channelId = firstId, allowLeave = false })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _notifier.Verify(n => n.NotifyGroupProfileChangedAsync(firstId), Times.Once);
        _notifier.Invocations.Clear();
        Assert.That((await _client.PutAsJsonAsync("/api/admin/required-channel", new { channelId = secondId, allowLeave = true })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _notifier.Verify(n => n.NotifyGroupProfileChangedAsync(firstId), Times.Once);
        _notifier.Verify(n => n.NotifyGroupProfileChangedAsync(secondId), Times.Once);
        _notifier.Invocations.Clear();
        Assert.That((await _client.PutAsJsonAsync("/api/admin/required-channel", new { channelId = Guid.NewGuid() })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(_notifier.Invocations, Is.Empty);
    }

    private void Authorize(string tokenType = "access", string role = "User", Guid? userId = null)
    {
        var actorId = userId ?? _db.Bob;
        var sessionId = _session;
        if (actorId != _db.Bob)
        {
            var session = new UserSession { UserId = actorId, RefreshTokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddDays(1) };
            _db.Context.UserSessions.Add(session);
            _db.Context.SaveChanges();
            sessionId = session.Id;
        }
        var token = new JwtSecurityToken(claims: new[]
        {
            new Claim("userId", actorId.ToString()), new Claim("sid", sessionId.ToString()),
            new Claim("tokenType", tokenType), new Claim(ClaimTypes.Role, role)
        }, expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    [Test]
    public async Task GroupChangesArriveOverRealSignalRConnectionsForBothParticipants()
    {
        _client.Dispose();
        _factory.Dispose();
        _factory = new TestFactory(_db, null);
        _client = _factory.CreateClient();
        Authorize();
        var created = await _client.PostAsJsonAsync("/api/groups", new { name = "Realtime", isPrivate = false });
        var id = (await created.Content.ReadFromJsonAsync<GroupResponse>())!.Id;
        Authorize(userId: _db.Alice);
        await _client.PostAsync($"/api/groups/{id}/join", null);
        using var alice = await ConnectHubAsync();
        Authorize();
        using var bob = await ConnectHubAsync();
        Assert.That((await _client.PutAsJsonAsync($"/api/groups/{id}", new { name = "Live profile", isPrivate = true })).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        foreach (var connection in new[] { alice, bob })
        {
            var frame = await connection.ReadAsync();
            Assert.That(frame.GetProperty("target").GetString(), Is.EqualTo("GroupProfileChanged"));
            Assert.That(frame.GetProperty("arguments")[0].GetProperty("groupId").GetGuid(), Is.EqualTo(id));
        }
        Assert.That((await _client.PutAsync($"/api/groups/{id}/members/{_db.Alice}/administrator", null)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        foreach (var connection in new[] { alice, bob })
        {
            var frame = await connection.ReadAsync();
            Assert.That(frame.GetProperty("target").GetString(), Is.EqualTo("GroupMemberChanged"));
            var change = frame.GetProperty("arguments")[0];
            Assert.That(change.GetProperty("userId").GetGuid(), Is.EqualTo(_db.Alice));
            Assert.That(change.GetProperty("status").GetInt32(), Is.Zero);
            Assert.That(change.GetProperty("role").GetInt32(), Is.EqualTo(1));
        }
        Assert.That((await _client.PutAsync($"/api/groups/{id}/members/{_db.Alice}/ban", null)).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        foreach (var connection in new[] { alice, bob })
        {
            var change = (await connection.ReadAsync()).GetProperty("arguments")[0];
            Assert.That(change.GetProperty("status").GetInt32(), Is.EqualTo(1));
            Assert.That(change.GetProperty("role").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
        Authorize(userId: _db.Alice);
        Assert.That((await _client.GetAsync($"/api/groups/{id}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    private async Task<TestHubConnection> ConnectHubAsync()
    {
        var client = _factory.Server.CreateWebSocketClient();
        var authorization = _client.DefaultRequestHeaders.Authorization;
        client.ConfigureRequest = request => request.Headers.Authorization = authorization!.ToString();
        var connection = new TestHubConnection(await client.ConnectAsync(new Uri("ws://localhost/hubs/chats"), CancellationToken.None));
        await connection.SendAsync("{\"protocol\":\"json\",\"version\":1}");
        Assert.That((await connection.ReadAsync()).EnumerateObject().Count(), Is.Zero);
        // A completion proves OnConnectedAsync has finished joining the session group.
        await connection.SendAsync("{\"type\":1,\"invocationId\":\"ready\",\"target\":\"Unknown\",\"arguments\":[]}");
        Assert.That((await connection.ReadAsync()).GetProperty("type").GetInt32(), Is.EqualTo(3));
        return connection;
    }

    private sealed class TestHubConnection(WebSocket socket) : IDisposable
    {
        private string _pending = "";
        public Task SendAsync(string json) => socket.SendAsync(Encoding.UTF8.GetBytes(json + '\u001e'), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<JsonElement> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new byte[4096];
            while (true)
            {
                var separator = _pending.IndexOf('\u001e');
                if (separator >= 0)
                {
                    using var document = JsonDocument.Parse(_pending[..separator]);
                    _pending = _pending[(separator + 1)..];
                    if (document.RootElement.TryGetProperty("type", out var type) && type.GetInt32() == 6) continue;
                    return document.RootElement.Clone();
                }
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Text));
                _pending += Encoding.UTF8.GetString(buffer, 0, result.Count);
            }
        }

        public void Dispose() => socket.Dispose();
    }

    private sealed class TestFactory(TestDatabase db, IChatNotificationService? notifier) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Firebase:Enabled", "false");
            builder.UseSetting("JwtAccessOption:SecretKey", Secret);
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.RemoveAll<IDataProtectionProvider>();
                services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                var worker = services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ChatPushWorker));
                services.Remove(worker);
                services.RemoveAll<DbContextOptions<GovorDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<GovorDbContext>>();
                services.AddDbContext<GovorDbContext>(db.ConfigureOptions);
                if (notifier is not null)
                {
                    services.RemoveAll<IChatNotificationService>();
                    services.AddSingleton(notifier);
                }
                services.RemoveAll<IStorageService>();
                services.AddSingleton<IStorageService>(new TestReactionStorage());
            });
        }
    }
}
