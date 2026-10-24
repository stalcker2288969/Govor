using AutoMapper;
using Govor.API.Common.SignalR.Helpers;
using Govor.API.Hubs;
using Govor.API.Hubs.Infrastructure;
using Govor.Application.Exceptions.VerifyFriendship;
using Govor.Application.Infrastructure.Common;
using Govor.Application.Messages;
using Govor.Application.Messages.Parameters;
using Govor.Application.Reactions;
using Govor.Contracts.Requests;
using Govor.Contracts.Requests.SignalR;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain.Common;
using Govor.Domain.Models.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;

namespace Govor.API.Tests.Unit.Hubs;

[TestFixture]
public class ChatsHubTests
{
    private readonly Guid _user = Guid.NewGuid(), _chat = Guid.NewGuid();
    private readonly DateTime _now = new(2026,10,24,0,0,0,DateTimeKind.Utc);
    private Mock<IMessageReadingService> _read = null!;
    private Mock<IMessageSendingService> _send = null!;
    private Mock<IMessageEditingService> _edit = null!;
    private Mock<IMessageRemovingService> _remove = null!;
    private Mock<IMessageReactionService> _reactions = null!;
    private Mock<IChatNotificationService> _notifier = null!;
    private Mock<IHubUserAccessor> _userAccessor = null!;
    private Mock<IConnectionManager> _connections = null!;
    private Mock<HubCallerContext> _context = null!;
    private Mock<IGroupManager> _groups = null!;
    private Mock<IMapper> _mapper = null!;
    private ChatsHub _hub = null!;
    private Message Message() => new() { Id=Guid.NewGuid(),SenderId=_user,RecipientId=_chat,RecipientType=RecipientType.User,EncryptedContent="cipher",SentAt=_now };

    [SetUp]
    public void Setup()
    {
        _read=new(); _send=new(); _edit=new(); _remove=new(); _reactions=new(); _notifier=new(); _connections=new(); _groups=new(); _mapper=new(); _context=new(); _userAccessor=new();
        _userAccessor.Setup(x=>x.GetUserId(It.IsAny<HubCallerContext>(),It.IsAny<bool>())).Returns(_user);
        var clock=new Mock<INowDateTimeProvider>(); clock.SetupGet(x=>x.Now).Returns(_now);
        _context.SetupGet(x=>x.ConnectionId).Returns("connection");
        _context.SetupGet(x=>x.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim("sid",_chat.ToString())})));
        _hub=new(NullLogger<ChatsHub>.Instance,_read.Object,_send.Object,_edit.Object,_remove.Object,_userAccessor.Object,_notifier.Object,clock.Object,_connections.Object,_reactions.Object,_mapper.Object) {Context=_context.Object,Groups=_groups.Object};
    }
    [TearDown] public void TearDown()=>_hub.Dispose();

    [TestCase(false)] [TestCase(true)]
    public async Task LifecycleMaintainsConnectionAndSessionGroup(bool error)
    {
        await _hub.OnConnectedAsync(); await _hub.OnDisconnectedAsync(error?new Exception("disconnect"):null);
        _connections.Verify(x=>x.OnConnectedAsync("connection",_user),Times.Once);
        _connections.Verify(x=>x.OnDisconnectedAsync("connection",_user),Times.Once);
        _groups.Verify(x=>x.AddToGroupAsync("connection",ChatHubConstants.GetSessionGroup(_chat),default),Times.Once);
    }
    [TestCase(false)] [TestCase(true)]
    public async Task MissingUserOrSessionAbortsConnection(bool missingUser)
    {
        if(missingUser) _userAccessor.Setup(x=>x.GetUserId(It.IsAny<HubCallerContext>(),It.IsAny<bool>())).Returns(Guid.Empty);
        else _context.SetupGet(x=>x.User).Returns(new ClaimsPrincipal());
        await _hub.OnConnectedAsync(); _context.Verify(x=>x.Abort(),Times.Once); _groups.VerifyNoOtherCalls();
    }

    [TestCase(false)] [TestCase(true)]
    public async Task SendMapsActorClockReplyAndMediaThenNotifies(bool media)
    {
        var message=Message(); var mediaId=Guid.NewGuid(); var reply=Guid.NewGuid();
        if (media) message.MediaAttachments = [new() { MediaFile = new() { Id = mediaId } }];
        _send.Setup(x=>x.SendMessageAsync(It.IsAny<SendMessage>())).ReturnsAsync(new SendMessageResult(true,null,message));
        var response=await _hub.Send(new() {RecipientId=_chat,EncryptedContent="cipher",ReplyToMessageId=reply,MediaAttachments=media?[new(){MediaId=mediaId,EncryptedKey="key"}]:null!});
        Assert.That(response.Status,Is.EqualTo(HubResultStatus.Success));
        Assert.That(response.Result!.ReplyToMessageId,Is.EqualTo(reply));
        Assert.That(response.Result.MediaAttachments.Select(m => m.Id), Is.EqualTo(media ? new[] { mediaId } : Array.Empty<Guid>()));
        _send.Verify(x=>x.SendMessageAsync(It.Is<SendMessage>(p=>p.FromUserId==_user && p.RecipientId==_chat && p.SendAt==_now && p.ReplyToMessageId==reply && p.Media.Count()==(media?1:0))),Times.Once);
        _notifier.Verify(x=>x.NotifyMessageSentAsync(response.Result!),Times.Once);
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
    public async Task InvalidSendNeverInvokesService(int kind)
    {
        MessageRequest? request=kind switch {0=>null,1=>new(){EncryptedContent=" "},2=>new(){EncryptedContent=new string('a',1_000_001)},_=>new(){EncryptedContent="",MediaAttachments=null!}};
        Assert.That((await _hub.Send(request!)).Status,Is.EqualTo(HubResultStatus.BadRequest)); _send.VerifyNoOtherCalls(); _notifier.VerifyNoOtherCalls();
    }

    [TestCase(0,HubResultStatus.Forbidden)] [TestCase(1,HubResultStatus.Unauthorized)]
    [TestCase(2,HubResultStatus.BadRequest)] [TestCase(3,HubResultStatus.NotFound)]
    [TestCase(4,HubResultStatus.BadRequest)] [TestCase(5,HubResultStatus.ServerError)]
    public async Task ServiceExceptionsMapToHubStatus(int kind,HubResultStatus expected)
    {
        Exception error=kind switch {0=>new UnauthorizedAccessException(),1=>new FriendshipException("friend"),2=>new ArgumentException("invalid"),3=>new KeyNotFoundException(),4=>new InvalidOperationException("conflict"),_=>new Exception("private detail")};
        _send.Setup(x=>x.SendMessageAsync(It.IsAny<SendMessage>())).ReturnsAsync(new SendMessageResult(false,error,null!));
        var response=await _hub.Send(new(){EncryptedContent="cipher"}); Assert.That(response.Status,Is.EqualTo(expected));
        if(kind==5) Assert.That(response.ErrorMessage,Does.Not.Contain("private detail"));
        _notifier.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SendFailureWithoutExceptionIsBadRequest()
    {
        _send.Setup(x=>x.SendMessageAsync(It.IsAny<SendMessage>())).ReturnsAsync(new SendMessageResult(false,null,null!));
        Assert.That((await _hub.Send(new(){EncryptedContent="cipher"})).Status,Is.EqualTo(HubResultStatus.BadRequest));
    }

    [Test]
    public async Task ReadMapsCurrentReadersViewAndNotifies()
    {
        var message=Message(); var view=new MessageView{Id=Guid.NewGuid(),UserId=_user,ViewedAt=_now}; message.MessageViews=[new(){UserId=Guid.NewGuid()},view];
        _read.Setup(x=>x.ReadMessageAsync(_user,message.Id)).ReturnsAsync(message);
        var result=await _hub.Read(new(){MessageId=message.Id});
        Assert.That(result.Result!.ViewId,Is.EqualTo(view.Id)); Assert.That(result.Result.ReaderId,Is.EqualTo(_user));
        _notifier.Verify(x=>x.NotifyMessageWasReadAsync(result.Result!),Times.Once);
    }
    [Test]
    public async Task ReadFailureDoesNotNotify()
    {
        _read.Setup(x=>x.ReadMessageAsync(_user,It.IsAny<Guid>())).ReturnsAsync(Govor.Domain.Common.Result.Failure<Message>(Error.NotFound("missing","missing")));
        Assert.That((await _hub.Read(new())).Status,Is.EqualTo(HubResultStatus.NotFound)); _notifier.VerifyNoOtherCalls();
    }

    [TestCase(false)] [TestCase(true)]
    public async Task ReadChatMapsResultOrFailure(bool failure)
    {
        var value=new ChatReadResult(_chat,RecipientType.User,_user,_now,null,null,[Guid.NewGuid()],2,3);
        _read.Setup(x=>x.ReadChatAsync(_user,_chat,RecipientType.User,null,null)).ReturnsAsync(failure?Govor.Domain.Common.Result.Failure<ChatReadResult>(Error.Forbidden("denied","denied")):value);
        var result=await _hub.ReadChat(_chat,new(){RecipientType=RecipientType.User});
        Assert.That(result.Status,Is.EqualTo(failure?HubResultStatus.Forbidden:HubResultStatus.Success));
        if(failure) _notifier.VerifyNoOtherCalls(); else {Assert.That(result.Result!.ReadCount,Is.EqualTo(2)); Assert.That(result.Result.UnreadCount,Is.EqualTo(3)); _notifier.Verify(x=>x.NotifyChatReadAsync(result.Result!),Times.Once);}
    }

    [TestCase(RemoveMessageRequestType.HideForMe,false)] [TestCase(RemoveMessageRequestType.ForceRemove,true)] [TestCase((RemoveMessageRequestType)99,false)]
    public async Task RemovalMapsScopeAndNotifies(RemoveMessageRequestType type,bool force)
    {
        var message=Message();
        _remove.Setup(x=>x.DeleteMessageAsync(It.IsAny<DeleteMessage>())).ReturnsAsync(message);
        var result=await _hub.Remove(new(){MessageId=message.Id,RequestType=type});
        Assert.That(result.Status,Is.EqualTo(HubResultStatus.Success));
        _remove.Verify(x=>x.DeleteMessageAsync(new DeleteMessage(_user,message.Id,force)),Times.Once);
        _notifier.Verify(x=>x.NotifyMessageRemovedAsync(result.Result!),Times.Once);
    }
    [Test]
    public async Task RemovalFailureDoesNotNotify()
    {
        _remove.Setup(x=>x.DeleteMessageAsync(It.IsAny<DeleteMessage>())).ReturnsAsync(Govor.Domain.Common.Result.Failure<Message>(Error.Forbidden("denied","denied")));
        Assert.That((await _hub.Remove(new())).Status,Is.EqualTo(HubResultStatus.Forbidden)); _notifier.VerifyNoOtherCalls();
    }

    [TestCase(false)] [TestCase(true)]
    public async Task EditSuccessUsesClockAndPreservesRouting(bool noOriginal)
    {
        var message=Message(); message.ReplyToMessageId=Guid.NewGuid();
        _edit.Setup(x=>x.EditMessageAsync(It.IsAny<EditMessage>())).ReturnsAsync(new EditMessageResult(true,null,noOriginal?null:message));
        var result=await _hub.Edit(new(){MessageId=message.Id,NewEncryptedContent="new cipher"});
        if(noOriginal){Assert.That(result.Status,Is.EqualTo(HubResultStatus.BadRequest)); _notifier.VerifyNoOtherCalls();return;}
        Assert.That(result.Result!.ReplyToMessageId,Is.EqualTo(message.ReplyToMessageId)); Assert.That(result.Result.EditedAt,Is.EqualTo(_now));
        _edit.Verify(x=>x.EditMessageAsync(new EditMessage(_user,message.Id,"new cipher",_now)),Times.Once);
        _notifier.Verify(x=>x.NotifyMessageEditedAsync(result.Result!),Times.Once);
    }

    [TestCase(false,false)] [TestCase(true,false)] [TestCase(true,true)]
    public async Task ReactionsNotifyOnlyOnChange(bool changed,bool remove)
    {
        var message=Guid.NewGuid(); Guid? reaction=remove?null:Guid.NewGuid();
        var state=new MessageReactionState(message,_chat,RecipientType.User,1,_user,null,[],changed);
        _reactions.Setup(x=>x.SetAsync(_user,message,reaction)).ReturnsAsync(state);
        var mapped=new MessageReactionsChangedResponse(); _mapper.Setup(x=>x.Map<MessageReactionsChangedResponse>(state)).Returns(mapped);
        var result=remove?await _hub.RemoveReaction(message):await _hub.React(message,new(){ReactionId=reaction!.Value});
        Assert.That(result.Result,Is.SameAs(mapped));
        _notifier.Verify(x=>x.NotifyMessageReactionsChangedAsync(mapped),changed?Times.Once():Times.Never());
    }
    [Test]
    public async Task ReactionFailureDoesNotNotify()
    {
        _reactions.Setup(x=>x.SetAsync(_user,It.IsAny<Guid>(),It.IsAny<Guid?>())).ReturnsAsync(Govor.Domain.Common.Result.Failure<MessageReactionState>(Error.Forbidden("denied","denied")));
        Assert.That((await _hub.RemoveReaction(Guid.NewGuid())).Status,Is.EqualTo(HubResultStatus.Forbidden)); _notifier.VerifyNoOtherCalls();
    }
    [Test]
    public async Task NullRequestsAreRejected()
    {
        Assert.That((await _hub.Read(null!)).Status,Is.EqualTo(HubResultStatus.BadRequest));
        Assert.That((await _hub.ReadChat(_chat,null!)).Status,Is.EqualTo(HubResultStatus.BadRequest));
        Assert.That((await _hub.Remove(null!)).Status,Is.EqualTo(HubResultStatus.BadRequest));
        Assert.That((await _hub.Edit(null!)).Status,Is.EqualTo(HubResultStatus.BadRequest));
        Assert.That((await _hub.React(Guid.NewGuid(),null!)).Status,Is.EqualTo(HubResultStatus.BadRequest));
        _notifier.VerifyNoOtherCalls();
    }
}
