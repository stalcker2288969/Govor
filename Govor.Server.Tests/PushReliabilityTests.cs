using Govor.API.Hubs.Infrastructure;
using Govor.Application.Infrastructure.Common;
using Govor.Application.Messages;
using Govor.Application.Messages.Parameters;
using Govor.Application.Profiles;
using Govor.Application.PushNotifications;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Server.Tests;

[TestFixture]
public class PushReliabilityTests
{
    private TestDatabase _db = null!;
    private Mock<INowDateTimeProvider> _clock = null!;
    private PushTokenService _tokens = null!;
    private DateTime _now;

    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        _now = DateTime.UtcNow.AddMinutes(1);
        _clock = new Mock<INowDateTimeProvider>();
        _clock.SetupGet(c => c.Now).Returns(() => _now);
        _tokens = new PushTokenService(_db.Context, _clock.Object, NullLogger<PushTokenService>.Instance);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task SavedMessageHasDurablePushAndFailureRetriesAfterBackoff()
    {
        var sender = new MessageSendingService(_db.Context, NullLogger<MessageSendingService>.Instance);
        var sent = await sender.SendMessageAsync(new SendMessage(await EncryptedHelloAsync(), null, _db.ChatId,
            Govor.Domain.Models.Messages.RecipientType.User, _db.Alice, _now, []));
        Assert.That(sent.IsSuccess, Is.True);
        Assert.That(await _db.Context.ChatPushNotifications.CountAsync(), Is.EqualTo(1));
        var notifier = new Mock<IChatNotificationService>();
        notifier.SetupSequence(n => n.DeliverPushAsync(It.IsAny<UserMessageResponse>())).ReturnsAsync(false).ReturnsAsync(true);
        var dispatcher = new ChatPushDispatcher(_db.Context, notifier.Object, _clock.Object);
        await dispatcher.DispatchAsync();
        var pending = await _db.Context.ChatPushNotifications.AsNoTracking().SingleAsync();
        Assert.That(pending.Attempts, Is.EqualTo(1));
        Assert.That(pending.NextAttemptAt, Is.GreaterThan(_now));
        await dispatcher.DispatchAsync();
        notifier.Verify(n => n.DeliverPushAsync(It.IsAny<UserMessageResponse>()), Times.Once);
        _now = pending.NextAttemptAt.AddSeconds(1);
        // A new scope simulates worker restart, while the queued row remains durable.
        using var restarted = _db.CreateContext();
        await new ChatPushDispatcher(restarted, notifier.Object, _clock.Object).DispatchAsync();
        notifier.Verify(n => n.DeliverPushAsync(It.IsAny<UserMessageResponse>()), Times.Exactly(2));
        Assert.That(await _db.Context.ChatPushNotifications.CountAsync(), Is.Zero);
    }

    private async Task<string> EncryptedHelloAsync()
    {
        var directory = new Govor.Application.E2ee.E2eeService(_db.Context);
        var devices = new List<Govor.Security.DeviceKey>();
        var privateKeys = Govor.Security.E2eeProtocol.GenerateKeys();
        foreach (var user in new[] { _db.Alice, _db.Bob })
        {
            var session = new UserSession { UserId = user, RefreshTokenHash = Guid.NewGuid().ToString(),
                ExpiresAt = DateTime.UtcNow.AddDays(30) };
            _db.Context.UserSessions.Add(session);
            await _db.Context.SaveChangesAsync();
            var key = Govor.Security.E2eeProtocol.PublicKey(user == _db.Alice ? privateKeys :
                Govor.Security.E2eeProtocol.GenerateKeys(), user, session.Id);
            await directory.RegisterAsync(user, session.Id, key);
            devices.Add(key);
        }
        return Govor.Security.E2eeProtocol.Encrypt(new("hello", []), privateKeys, devices[0],
            await directory.GetConversationKeysAsync(_db.Alice, _db.ChatId, Govor.Domain.Models.Messages.RecipientType.User), Guid.NewGuid());
    }

    [Test]
    public async Task ReassignedDeviceTokenCannotNotifyThePreviousAccount()
    {
        var aliceSession = AddSession(_db.Alice);
        var bobSession = AddSession(_db.Bob);
        await _tokens.AddOrUpdateTokenAsync(_db.Alice, aliceSession, "device-token", "android");
        await _tokens.AddOrUpdateTokenAsync(_db.Bob, bobSession, "other-token", "android");
        var result = await _tokens.AddOrUpdateTokenAsync(_db.Bob, bobSession, "device-token", "ios");
        Assert.That(result.IsSuccess, Is.True);
        Assert.That((await _tokens.GetStringsActiveTokensAsync(_db.Alice)).Value, Is.Empty);
        Assert.That((await _tokens.GetStringsActiveTokensAsync(_db.Bob)).Value, Is.EqualTo(new[] { "device-token" }));
        Assert.That(await _db.Context.UserPushTokens.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task InactiveTokenCanBeReactivatedButForeignSessionCannotRegisterIt()
    {
        var session = AddSession(_db.Bob);
        await _tokens.AddOrUpdateTokenAsync(_db.Bob, session, "token", "android");
        await _tokens.DeactivateTokenBySessionAsync(session);
        _db.Context.ChangeTracker.Clear();
        Assert.That((await _tokens.GetStringsActiveTokensAsync(_db.Bob)).Value, Is.Empty);
        await _tokens.AddOrUpdateTokenAsync(_db.Bob, session, "token", "android");
        Assert.That((await _tokens.GetStringsActiveTokensAsync(_db.Bob)).Value, Has.Count.EqualTo(1));
        var foreign = await _tokens.AddOrUpdateTokenAsync(_db.Alice, session, "stolen", "android");
        Assert.That(foreign.Error.Type, Is.EqualTo(ErrorType.Forbidden));
    }

    [Test]
    public async Task ExpiredOrRevokedSessionTokensAreExcludedWithoutWaitingForCleanup()
    {
        var session = AddSession(_db.Bob);
        await _tokens.AddOrUpdateTokenAsync(_db.Bob, session, "token", "android");
        await _db.Context.UserSessions.Where(s => s.Id == session)
            .ExecuteUpdateAsync(s => s.SetProperty(row => row.IsRevoked, true));
        Assert.That((await _tokens.GetStringsActiveTokensAsync(_db.Bob)).Value, Is.Empty);
        Assert.That((await _tokens.GetUsersStringsActiveTokensAsync(new[] { _db.Bob })).Value, Is.Empty);
        Assert.That((await _tokens.GetActiveTokenBySessionAsync(session)).Value, Is.Null);
    }

    [Test]
    public async Task ForeignAttachmentCannotBePublishedAsAnAvatar()
    {
        var media = new MediaFile { Id = Guid.NewGuid(), UploaderId = _db.Alice,
            OwnerType = MediaOwnerType.Message, Url = "test", MineType = "image/png" };
        _db.Context.MediaFiles.Add(media);
        _db.Context.SaveChanges();
        var profiles = new ProfileService(_db.Context, NullLogger<ProfileService>.Instance);
        var forbidden = await profiles.SetNewIcon(_db.Bob, media.Id);
        Assert.That(forbidden.Error.Type, Is.EqualTo(ErrorType.Forbidden));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ExpiredWorkerCannotDeleteOrRescheduleANewerLease(bool delivered)
    {
        var message = _db.AddMessage();
        _db.Context.ChatPushNotifications.Add(new Govor.Domain.Models.Messages.ChatPushNotification
        { MessageId = message.Id, NextAttemptAt = _now });
        await _db.Context.SaveChangesAsync();
        var notifier = new Mock<IChatNotificationService>();
        var secondWorker = new Mock<IChatNotificationService>();
        secondWorker.Setup(n => n.DeliverPushAsync(It.IsAny<UserMessageResponse>())).ReturnsAsync(false);
        DateTime retryAt = default;
        notifier.Setup(n => n.DeliverPushAsync(It.IsAny<UserMessageResponse>())).Returns(async () =>
        {
            _now = _now.AddMinutes(3);
            using var otherContext = _db.CreateContext();
            await new ChatPushDispatcher(otherContext, secondWorker.Object, _clock.Object).DispatchAsync();
            retryAt = (await otherContext.ChatPushNotifications.AsNoTracking().SingleAsync()).NextAttemptAt;
            _now = _now.AddSeconds(1);
            return delivered;
        });

        await new ChatPushDispatcher(_db.Context, notifier.Object, _clock.Object).DispatchAsync();

        var pending = await _db.Context.ChatPushNotifications.AsNoTracking().SingleAsync();
        Assert.That(pending.Attempts, Is.EqualTo(2));
        Assert.That(pending.NextAttemptAt, Is.EqualTo(retryAt));
    }

    [Test]
    public async Task NotificationExceptionDoesNotBlockRemainingMessagesInBatch()
    {
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var message = _db.AddMessage();
            _db.Context.ChatPushNotifications.Add(new Govor.Domain.Models.Messages.ChatPushNotification
            { MessageId = message.Id, NextAttemptAt = _now });
        }
        await _db.Context.SaveChangesAsync();
        var notifier = new Mock<IChatNotificationService>();
        notifier.SetupSequence(n => n.DeliverPushAsync(It.IsAny<UserMessageResponse>()))
            .ThrowsAsync(new HttpRequestException("offline")).ReturnsAsync(true);

        await new ChatPushDispatcher(_db.Context, notifier.Object, _clock.Object).DispatchAsync();

        notifier.Verify(n => n.DeliverPushAsync(It.IsAny<UserMessageResponse>()), Times.Exactly(2));
        var pending = await _db.Context.ChatPushNotifications.AsNoTracking().SingleAsync();
        Assert.That(pending.NextAttemptAt, Is.EqualTo(_now.AddSeconds(2)));
    }

    private Guid AddSession(Guid user)
    {
        var session = new UserSession { UserId = user, ExpiresAt = _now.AddDays(1), RefreshTokenHash = Guid.NewGuid().ToString() };
        _db.Context.UserSessions.Add(session);
        _db.Context.SaveChanges();
        return session.Id;
    }
}
