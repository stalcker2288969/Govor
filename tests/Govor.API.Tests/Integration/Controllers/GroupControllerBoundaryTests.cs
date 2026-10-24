using AutoMapper;
using Govor.API.Controllers;
using Govor.API.Controllers.AdminStuff;
using Govor.API.Hubs.Infrastructure;
using Govor.Application.Groups;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.Messages;
using Govor.Application.Reactions;
using Govor.Contracts.Requests;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Govor.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Govor.API.Tests.Integration.Controllers;

[TestFixture]
public class GroupControllerBoundaryTests
{
    [Test]
    public async Task MembersAndInvitationsMapContractsAndRevokeUsesCurrentActor()
    {
        var actor = Guid.NewGuid(); var group = Guid.NewGuid(); var invitation = Guid.NewGuid();
        var user = new Mock<ICurrentUserService>(); user.Setup(x => x.GetCurrentUserId()).Returns(actor);
        var groups = new Mock<IGroupManagementService>(); var mapper = new Mock<IMapper>();
        var members = new List<GroupMember>(); var invitations = new List<GroupInvitation>();
        var memberDtos = new List<Govor.Contracts.Responses.GroupMemberResponse>();
        var invitationDtos = new List<Govor.Contracts.Responses.GroupInvitationResponse>();
        groups.Setup(x => x.MembersAsync(actor, group, true, 3, 7)).ReturnsAsync(members);
        groups.Setup(x => x.InvitationsAsync(actor, group)).ReturnsAsync(invitations);
        groups.Setup(x => x.RevokeInvitationAsync(actor, group, invitation)).ReturnsAsync(Result.Success());
        mapper.Setup(x => x.Map<List<Govor.Contracts.Responses.GroupMemberResponse>>(members)).Returns(memberDtos);
        mapper.Setup(x => x.Map<List<Govor.Contracts.Responses.GroupInvitationResponse>>(invitations)).Returns(invitationDtos);
        var controller = new GroupsController(groups.Object, user.Object, mapper.Object, null!, Mock.Of<IMessageRemovingService>(), Mock.Of<IChatNotificationService>(), Mock.Of<IGroupAvatarService>());
        Assert.That(((OkObjectResult)await controller.Members(group, true, 3, 7)).Value, Is.SameAs(memberDtos));
        Assert.That(((OkObjectResult)await controller.Invitations(group)).Value, Is.SameAs(invitationDtos));
        Assert.That(((StatusCodeResult)await controller.RevokeInvitation(group, invitation)).StatusCode, Is.EqualTo(204));
        groups.Verify(x => x.RevokeInvitationAsync(actor, group, invitation), Times.Once);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RequiredChannelReadMapsSettingsAndFailuresPreventMutation(bool failure)
    {
        var actor = Guid.NewGuid();
        var groups = new Mock<IGroupManagementService>();
        var user = new Mock<ICurrentUserService>();
        user.Setup(x => x.GetCurrentUserId()).Returns(actor);
        var settings = new ServerCommunitySettings { RequiredChannelId = Guid.NewGuid(), AllowLeave = true };
        groups.Setup(x => x.GetRequiredChannelAsync(actor)).ReturnsAsync(failure
            ? Result.Failure<ServerCommunitySettings>(Error.Forbidden("denied", "denied"))
            : settings);
        var notifier = new Mock<IChatNotificationService>(MockBehavior.Strict);
        var controller = new RequiredChannelController(groups.Object, user.Object, notifier.Object);
        var response = (ObjectResult)await controller.Get();
        Assert.That(response.StatusCode, Is.EqualTo(failure ? 403 : 200));
        if (failure)
        {
            Assert.That(((ObjectResult)await controller.Set(new())).StatusCode, Is.EqualTo(403));
            groups.Verify(x => x.SetRequiredChannelAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>()),
                Times.Never);
        }
        else
        {
            Assert.That(response.Value!.GetType().GetProperty("channelId")!.GetValue(response.Value),
                Is.EqualTo(settings.RequiredChannelId));
        }

        notifier.VerifyNoOtherCalls();
    }

    [Test]
    public async Task GroupReadsForwardQueryAndPaging()
    {
        using var db = new TestDatabase();
        var groups = new Mock<IGroupManagementService>();
        var user = new Mock<ICurrentUserService>();
        user.Setup(x => x.GetCurrentUserId()).Returns(db.Alice);
        var mapper = new Mock<IMapper>();
        var list = new List<GroupSummary>();
        var mapped = new List<Govor.Contracts.Responses.GroupResponse>();
        groups.Setup(x => x.MineAsync(db.Alice, 3, 7)).ReturnsAsync(list);
        groups.Setup(x => x.SearchAsync(db.Alice, "needle", 4, 8)).ReturnsAsync(list);
        mapper.Setup(x => x.Map<List<Govor.Contracts.Responses.GroupResponse>>(list)).Returns(mapped);
        var controller = new GroupsController(groups.Object, user.Object, mapper.Object, db.Context,
            Mock.Of<IMessageRemovingService>(), Mock.Of<IChatNotificationService>(), Mock.Of<IGroupAvatarService>());
        Assert.That(((OkObjectResult)await controller.Mine(3, 7)).Value, Is.SameAs(mapped));
        Assert.That(((OkObjectResult)await controller.Search("needle", 4, 8)).Value, Is.SameAs(mapped));
        groups.Verify(x => x.MineAsync(db.Alice, 3, 7), Times.Once);
        groups.Verify(x => x.SearchAsync(db.Alice, "needle", 4, 8), Times.Once);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AvatarRemovalNotifiesOnlyAfterSuccess(bool failure)
    {
        using var db = new TestDatabase();
        var user = new Mock<ICurrentUserService>();
        user.Setup(x => x.GetCurrentUserId()).Returns(db.Alice);
        var avatars = new Mock<IGroupAvatarService>();
        avatars.Setup(x => x.RemoveAsync(db.Alice, db.ChatId))
            .ReturnsAsync(failure ? Result.Failure(Error.Forbidden("denied", "denied")) : Result.Success());
        var notifier = new Mock<IChatNotificationService>();
        var controller = new GroupsController(Mock.Of<IGroupManagementService>(), user.Object, Mock.Of<IMapper>(),
            db.Context, Mock.Of<IMessageRemovingService>(), notifier.Object, avatars.Object);
        var response = await controller.RemoveAvatar(db.ChatId);
        Assert.That(failure ? ((ObjectResult)response).StatusCode : ((StatusCodeResult)response).StatusCode,
            Is.EqualTo(failure ? 403 : 204));
        notifier.Verify(x => x.NotifyGroupProfileChangedAsync(db.ChatId), failure ? Times.Never() : Times.Once());
    }

    [TestCase(0)]
    [TestCase(2_097_153)]
    public async Task AvatarLengthRejectsBeforeService(int size)
    {
        var file = new Mock<IFormFile>();
        file.SetupGet(x => x.Length).Returns(size);
        var avatars = new Mock<IGroupAvatarService>(MockBehavior.Strict);
        var controller = new GroupsController(Mock.Of<IGroupManagementService>(), Mock.Of<ICurrentUserService>(),
            Mock.Of<IMapper>(), null!, Mock.Of<IMessageRemovingService>(), Mock.Of<IChatNotificationService>(),
            avatars.Object);
        Assert.That(await controller.UploadAvatar(Guid.NewGuid(), new() { File = file.Object }),
            Is.TypeOf<BadRequestObjectResult>());
        avatars.VerifyNoOtherCalls();
    }

    [Test]
    public async Task FailedGroupMessageRemovalNeverNotifies()
    {
        using var db = new TestDatabase();
        var message = db.AddMessage(type: RecipientType.Group);
        var user = new Mock<ICurrentUserService>();
        user.Setup(x => x.GetCurrentUserId()).Returns(db.Alice);
        var remover = new Mock<IMessageRemovingService>();
        remover.Setup(x => x.DeleteMessageAsync(It.IsAny<Govor.Application.Messages.Parameters.DeleteMessage>()))
            .ReturnsAsync(Result.Failure<Message>(Error.Forbidden("denied", "denied")));
        var notifier = new Mock<IChatNotificationService>(MockBehavior.Strict);
        var controller = new GroupsController(Mock.Of<IGroupManagementService>(), user.Object, Mock.Of<IMapper>(),
            db.Context, remover.Object, notifier.Object, Mock.Of<IGroupAvatarService>());
        Assert.That(((ObjectResult)await controller.RemoveMessage(db.ChatId, message.Id)).StatusCode, Is.EqualTo(403));
        notifier.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ChannelReactionReadMapsPolicy()
    {
        var actor = Guid.NewGuid();
        var group = Guid.NewGuid();
        var user = new Mock<ICurrentUserService>();
        user.Setup(x => x.GetCurrentUserId()).Returns(actor);
        var service = new Mock<IMessageReactionService>();
        var policy = new ChannelReactionPolicy(group, Govor.Domain.Models.Reactions.ChannelReactionMode.All, 2, []);
        service.Setup(x => x.GetPolicyAsync(actor, group)).ReturnsAsync(policy);
        var mapper = new Mock<IMapper>();
        var dto = new Govor.Contracts.Responses.SignalR.ChannelReactionPolicyResponse();
        mapper.Setup(x => x.Map<Govor.Contracts.Responses.SignalR.ChannelReactionPolicyResponse>(policy)).Returns(dto);
        var controller = new ChannelReactionsController(service.Object, user.Object, mapper.Object,
            Mock.Of<IChatNotificationService>());
        Assert.That(((OkObjectResult)await controller.Get(group)).Value, Is.SameAs(dto));
    }
}

