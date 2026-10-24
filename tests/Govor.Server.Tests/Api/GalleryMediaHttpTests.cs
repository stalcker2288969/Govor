using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Govor.API.Hubs;
using Govor.Application.E2ee;
using Govor.Application.Medias;
using Govor.Contracts.Requests.SignalR;
using Govor.Contracts.Responses;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models.Users;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Govor.Server.Tests;

public partial class ReadApiTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task GalleryAttachmentUploadHubHistoryDownloadAndDeletionRoundTrip(bool mediaOnly)
    {
        Authorize();
        var bob = E2eeProtocol.GenerateKeys();
        var alice = E2eeProtocol.GenerateKeys();
        var e2ee = new E2eeService(_db.Context);
        var own = E2eeProtocol.PublicKey(bob, _db.Bob, _session);
        await e2ee.RegisterAsync(_db.Bob, _session, own);
        var aliceSession = new UserSession { UserId = _db.Alice, RefreshTokenHash = Guid.NewGuid().ToString(), ExpiresAt = DateTime.UtcNow.AddDays(1) };
        _db.Context.UserSessions.Add(aliceSession);
        await _db.Context.SaveChangesAsync();
        await e2ee.RegisterAsync(_db.Alice, aliceSession.Id, E2eeProtocol.PublicKey(alice, _db.Alice, aliceSession.Id));

        var original = "gallery photo bytes"u8.ToArray();
        var (encrypted, key) = E2eeProtocol.EncryptAttachment(original);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(encrypted), "FromFile", "private-location.jpg");
        form.Add(new StringContent("0"), "Type");
        form.Add(new StringContent("0"), "OwnerType");
        form.Add(new StringContent(E2eeProtocol.EncryptedMimeType), "MimeType");
        var uploaded = await _client.PostAsync("/api/media/upload", form);
        Assert.That(uploaded.StatusCode, Is.EqualTo(HttpStatusCode.OK), await uploaded.Content.ReadAsStringAsync());
        var file = (await uploaded.Content.ReadFromJsonAsync<MediaUploadResult>())!;
        Assert.That(file.Url, Does.Not.Contain("private-location"));
        Assert.That((await _client.GetAsync($"/api/media/download/{file.MediaId}")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        var id = Guid.NewGuid();
        var caption = mediaOnly ? "" : "Подпись к фото";
        var envelope = E2eeProtocol.Encrypt(new(caption, [new(file.MediaId, key, "photo.jpg", "image/jpeg")]), bob, own,
            await e2ee.GetConversationKeysAsync(_db.Bob, _db.ChatId, RecipientType.User), id);
        using var scope = _factory.Services.CreateScope();
        using var hub = ActivatorUtilities.CreateInstance<ChatsHub>(scope.ServiceProvider);
        var caller = new Mock<HubCallerContext>();
        caller.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("userId", _db.Bob.ToString()), new Claim("sid", _session.ToString())])));
        hub.Context = caller.Object;
        var request = new MessageRequest { RecipientId = _db.ChatId, RecipientType = RecipientType.User,
            EncryptedContent = envelope, MediaAttachments = [new() { MediaId = file.MediaId }] };
        var sent = await hub.Send(request);
        Assert.That(sent.Status, Is.EqualTo(HubResultStatus.Success), sent.ErrorMessage);
        Assert.That(sent.Result!.MediaAttachments.Select(a => a.Id), Is.EqualTo(new[] { file.MediaId }));
        Assert.That((await hub.Send(request)).Status, Is.EqualTo(HubResultStatus.Success));
        Assert.That(await _db.Context.MediaAttachments.CountAsync(a => a.MessageId == id), Is.EqualTo(1));
        var tampered = await hub.Send(request with { MediaAttachments = [] });
        Assert.That(tampered.Status, Is.EqualTo(HubResultStatus.BadRequest));
        var history = await _client.GetFromJsonAsync<List<MessageResponse>>($"/api/user/{_db.ChatId}/messages?Before=20&After=0");
        Assert.That(history!.Single().MediaAttachments.Single().MediaFileId, Is.EqualTo(file.MediaId));
        var plaintext = E2eeProtocol.Decrypt(history!.Single().EncryptedContent, alice, id, _db.ChatId, 0, _db.Bob);
        Assert.That(plaintext.Text, Is.EqualTo(caption));
        Assert.That(plaintext.Attachments.Single().Key, Is.EqualTo(key));
        var download = await _client.GetAsync($"/api/media/download/{file.MediaId}");
        Assert.That(download.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(download.Headers.CacheControl!.NoStore, Is.True);
        Assert.That(E2eeProtocol.DecryptAttachment(await download.Content.ReadAsByteArrayAsync(), key), Is.EqualTo(original));

        Authorize(userId: _db.Outsider);
        Assert.That((await _client.GetAsync($"/api/media/download/{file.MediaId}")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Authorize();
        var removed = await hub.Remove(new() { MessageId = id, RequestType = RemoveMessageRequestType.ForceRemove });
        Assert.That(removed.Status, Is.EqualTo(HubResultStatus.Success), removed.ErrorMessage);
        Assert.That((await _client.GetAsync($"/api/media/download/{file.MediaId}")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await _db.Context.MediaAttachments.CountAsync(a => a.MessageId == id), Is.Zero);
    }

    [Test]
    public async Task ClosingAnOlderSessionOnTheSameDeviceKeepsCurrentSessionAuthenticated()
    {
        var current = await _db.Context.UserSessions.SingleAsync(s => s.Id == _session);
        current.DeviceInfo = "Same phone on Android 16";
        var older = new UserSession { UserId = _db.Bob, DeviceInfo = current.DeviceInfo,
            RefreshTokenHash = Guid.NewGuid().ToString(), ExpiresAt = DateTime.UtcNow.AddDays(1) };
        _db.Context.UserSessions.Add(older);
        await _db.Context.SaveChangesAsync();
        Authorize();
        Assert.That((await _client.DeleteAsync($"/api/session/close/{older.Id}")).IsSuccessStatusCode, Is.True);
        Assert.That((await _client.GetAsync("/api/session/all")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _db.Context.UserSessions.AsNoTracking().SingleAsync(s => s.Id == _session)).IsRevoked, Is.False);
        Assert.That((await _db.Context.UserSessions.AsNoTracking().SingleAsync(s => s.Id == older.Id)).IsRevoked, Is.True);
    }
}
