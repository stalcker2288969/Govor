using Govor.API.Hubs;
using Govor.API.Hubs.Infrastructure;
using Govor.Application.PrivateUserChats;
using Govor.Application.Profiles;
using Govor.Application.PushNotifications;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Govor.Tests.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Govor.Application.Infrastructure.Common;
using Microsoft.EntityFrameworkCore;

namespace Govor.API.Tests.Integration.Notifications;

[TestFixture]
public class NotificationBoundaryTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task MissingChatIsAcknowledgedButTemporaryLookupFailureRequiresRetry(bool missing)
    {
        using var db = new TestDatabase();
        var chats = new Mock<IUserPrivateChatsGetterService>();
        chats.Setup(x => x.GetPrivateChatAsync(db.ChatId)).ReturnsAsync(Result.Failure<PrivateChat>(
            missing ? Error.NotFound("missing", "missing") : Error.ServerError("database", "unavailable")));
        var push = new Mock<IPushNotificationService>(MockBehavior.Strict);
        var notifier = new ChatNotificationService(Mock.Of<IHubContext<ChatsHub>>(), push.Object, chats.Object,
            Mock.Of<IProfileService>(), db.Context, NullLogger<ChatNotificationService>.Instance);
        Assert.That(await notifier.DeliverPushAsync(new() { RecipientId = db.ChatId, RecipientType = RecipientType.User }), Is.EqualTo(missing));
        push.VerifyNoOtherCalls();
    }

    [Test]
    public async Task CancellationDuringPushLeavesClaimForLaterRetry()
    {
        using var db = new TestDatabase();
        using var cancellation = new CancellationTokenSource();
        var message = db.AddMessage();
        var now = DateTime.UtcNow;
        db.Context.ChatPushNotifications.Add(new() { MessageId = message.Id, NextAttemptAt = now.AddMinutes(-1) });
        await db.Context.SaveChangesAsync();
        var notifier = new Mock<IChatNotificationService>();
        notifier.Setup(x => x.DeliverPushAsync(It.IsAny<UserMessageResponse>())).Returns(() =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var clock = new Mock<INowDateTimeProvider>();
        clock.SetupGet(x => x.Now).Returns(now);
        var dispatcher = new ChatPushDispatcher(db.Context, notifier.Object, clock.Object);
        Assert.ThrowsAsync<OperationCanceledException>(() => dispatcher.DispatchAsync(cancellation.Token));
        var pending = await db.Context.ChatPushNotifications.AsNoTracking().SingleAsync();
        Assert.That(pending.Attempts, Is.EqualTo(1));
        Assert.That(pending.NextAttemptAt, Is.GreaterThan(now));
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
    public async Task PushHandlesNoRecipientsReadersMissingProfileAndProviderFailures(int mode)
    {
        using var db=new TestDatabase();var message=db.AddMessage();
        if(mode==1){db.Context.MessageViews.Add(new(){Id=Guid.NewGuid(),MessageId=message.Id,UserId=db.Bob,ViewedAt=DateTime.UtcNow});db.Context.SaveChanges();}
        var chats=new Mock<IUserPrivateChatsGetterService>();chats.Setup(x=>x.GetPrivateChatAsync(db.ChatId)).ReturnsAsync(mode==0?new PrivateChat{UserAId=db.Alice,UserBId=db.Alice}:new PrivateChat{UserAId=db.Alice,UserBId=db.Bob});
        var profiles=new Mock<IProfileService>();profiles.Setup(x=>x.GetUserProfileAsync(db.Alice)).ReturnsAsync(Result.Failure<UserProfile>(Error.NotFound("missing","missing")));
        var push=new Mock<IPushNotificationService>();
        if(mode==4) push.Setup(x=>x.SendToUsersAsync(It.IsAny<IEnumerable<Guid>>(),It.IsAny<string>(),It.IsAny<string>(),It.IsAny<string>(),It.IsAny<string>(),It.IsAny<Dictionary<string,string>?>())).ThrowsAsync(new Exception("transport"));
        else push.Setup(x=>x.SendToUsersAsync(It.IsAny<IEnumerable<Guid>>(),It.IsAny<string>(),It.IsAny<string>(),It.IsAny<string>(),It.IsAny<string>(),It.IsAny<Dictionary<string,string>?>())).ReturnsAsync(mode==3?Result.Failure(Error.ServerError("push","failure")):Result.Success());
        var notifier=new ChatNotificationService(Mock.Of<IHubContext<ChatsHub>>(),push.Object,chats.Object,profiles.Object,db.Context,NullLogger<ChatNotificationService>.Instance);
        var result=await notifier.DeliverPushAsync(new(){MessageId=message.Id,SenderId=db.Alice,RecipientId=db.ChatId,RecipientType=RecipientType.User});
        Assert.That(result,Is.EqualTo(mode<3));
        if(mode<2){push.VerifyNoOtherCalls();profiles.VerifyNoOtherCalls();}
        else push.Verify(x=>x.SendToUsersAsync(It.Is<IEnumerable<Guid>>(ids=>ids.SequenceEqual(new[]{db.Bob})),"Govor","New message","chat_messages",$"chat_{db.ChatId}",It.IsAny<Dictionary<string,string>?>()),Times.Once);
    }
    [Test]
    public async Task LegacyReadAndRemovalEventsReachActiveSessionAndMissingChatIsIgnored()
    {
        using var db=new TestDatabase();var sid=Guid.NewGuid();db.Context.UserSessions.Add(new(){Id=sid,UserId=db.Alice,ExpiresAt=DateTime.UtcNow.AddDays(1)});db.Context.SaveChanges();
        var proxy=new Mock<IClientProxy>();var clients=new Mock<IHubClients>();clients.Setup(x=>x.Groups(It.IsAny<IReadOnlyList<string>>())).Returns(proxy.Object);
        var hub=new Mock<IHubContext<ChatsHub>>();hub.SetupGet(x=>x.Clients).Returns(clients.Object);
        var chats=new Mock<IUserPrivateChatsGetterService>();chats.Setup(x=>x.GetPrivateChatAsync(db.ChatId)).ReturnsAsync(new PrivateChat{UserAId=db.Alice,UserBId=db.Bob});
        var notifier=new ChatNotificationService(hub.Object,Mock.Of<IPushNotificationService>(),chats.Object,Mock.Of<IProfileService>(),db.Context,NullLogger<ChatNotificationService>.Instance);
        await notifier.NotifyMessageWasReadAsync(new(){ViewId=Guid.NewGuid(),MessageId=Guid.NewGuid(),ReaderId=db.Alice,WhenWas=DateTime.UtcNow,RecipientId=db.ChatId,RecipientType=RecipientType.User});
        await notifier.NotifyMessageRemovedAsync(new(){MessageId=Guid.NewGuid(),SenderId=db.Alice,RecipientId=db.ChatId,RecipientType=RecipientType.User});
        await notifier.NotifyMessageEditedAsync(new(){MessageId=Guid.NewGuid(),EditorId=db.Alice,RecipientId=db.ChatId,RecipientType=RecipientType.User});
        foreach(var method in new[]{"MessageReaded","MessageRead","MessageRemoved","MessageEdited"})proxy.Verify(x=>x.SendCoreAsync(method,It.IsAny<object?[]>(),default),Times.Once);
        chats.Setup(x=>x.GetPrivateChatAsync(db.ChatId)).ReturnsAsync(Result.Failure<PrivateChat>(Error.NotFound("missing","missing")));
        proxy.Invocations.Clear();await notifier.NotifyChatReadAsync(new(){ChatId=db.ChatId,RecipientType=RecipientType.User});proxy.VerifyNoOtherCalls();
    }
}

