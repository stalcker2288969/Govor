using Govor.Application.PrivateUserChats;
using Govor.Application.Profiles;
using Govor.Application.PushNotifications;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain;
using Govor.Domain.Common;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Govor.API.Hubs.Infrastructure;

public class ChatNotificationService : IChatNotificationService
{
    private readonly IHubContext<ChatsHub> _hubContext;
    private readonly IPushNotificationService _notificationService;
    private readonly IUserPrivateChatsGetterService _chats;
    private readonly IProfileService _profiles;
    private readonly GovorDbContext _context;
    private readonly ILogger<ChatNotificationService> _logger;

    public ChatNotificationService(IHubContext<ChatsHub> hubContext,
        IPushNotificationService notificationService, IUserPrivateChatsGetterService chats,
        IProfileService profiles, GovorDbContext context, ILogger<ChatNotificationService> logger)
    {
        _hubContext = hubContext;
        _notificationService = notificationService;
        _chats = chats;
        _profiles = profiles;
        _context = context;
        _logger = logger;
    }

    public async Task NotifyMessageSentAsync(UserMessageResponse message)
    {
        await TryNotifyAsync(() => NotifyParticipantsAsync(message.RecipientId,
            message.RecipientType, ChatHubConstants.ReceiveMessage, message));
        await TryNotifyAsync(() => NotifyUsersAsync(new[] { message.SenderId }, ChatHubConstants.MessageSent, message));

    }

    public async Task<bool> DeliverPushAsync(UserMessageResponse message)
    {
        try
        {
            List<Guid> recipients;
            if (message.RecipientType == RecipientType.User)
            {
                var result = await _chats.GetPrivateChatAsync(message.RecipientId);
                if (result.IsFailure)
                    return result.Error.Type == ErrorType.NotFound;
                recipients = new[] { result.Value.UserAId, result.Value.UserBId }
                    .Where(id => id != message.SenderId).Distinct().ToList();
            }
            else
                recipients = await _context.GroupMemberships.AsNoTracking()
                    .Where(m => m.GroupId == message.RecipientId && m.UserId != message.SenderId && !m.IsBanned)
                    .Select(m => m.UserId).Distinct().ToListAsync();

            if (recipients.Count == 0)
                return true;
            var readers = await _context.MessageViews.AsNoTracking()
                .Where(v => v.MessageId == message.MessageId).Select(v => v.UserId).ToListAsync();
            recipients = recipients.Except(readers).ToList();
            if (recipients.Count == 0)
                return true;
            var profile = await _profiles.GetUserProfileAsync(message.SenderId);
            var title = profile.IsSuccess ? profile.Value.Username : "Govor";
            // Message content is encrypted; never send ciphertext or a guessed preview to FCM.
            var body = "New message";
            var data = new Dictionary<string, string>
            {
                ["chatId"] = message.RecipientId.ToString(),
                ["messageId"] = message.MessageId.ToString(),
                ["senderId"] = message.SenderId.ToString(),
                ["type"] = "chat_message",
                ["isGroup"] = message.RecipientType == RecipientType.Group ? "true" : "false"
            };
            var push = await _notificationService.SendToUsersAsync(recipients, title, body,
                "chat_messages", $"chat_{message.RecipientId}", data);
            if (push.IsFailure)
                _logger.LogWarning("Push failed for message {MessageId}: {Error}",
                    message.MessageId, push.Error);
            return push.IsSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deliver push for message {MessageId}", message.MessageId);
            return false;
        }
    }

    public async Task NotifyMessageWasReadAsync(MessageReadResponse response)
    {
        // Keep the existing wire name for old clients; new clients can use MessageRead.
        await TryNotifyAsync(() => NotifyParticipantsAsync(response.RecipientId,
            response.RecipientType, ChatHubConstants.MessageRead, response));
        await TryNotifyAsync(() => NotifyParticipantsAsync(response.RecipientId,
            response.RecipientType, "MessageRead", response));
    }

    public Task NotifyChatReadAsync(ChatReadResponse response) =>
        TryNotifyAsync(() => NotifyParticipantsAsync(response.ChatId,
            response.RecipientType, ChatHubConstants.ChatRead, response));

    public Task NotifyMessageReactionsChangedAsync(MessageReactionsChangedResponse response) =>
        TryNotifyAsync(() => NotifyParticipantsAsync(response.RecipientId,
            response.RecipientType, ChatHubConstants.MessageReactionsChanged, response));

    public Task NotifyChannelReactionPolicyChangedAsync(ChannelReactionPolicyResponse response) =>
        TryNotifyAsync(() => NotifyParticipantsAsync(response.GroupId,
            RecipientType.Group, ChatHubConstants.ChannelReactionPolicyChanged, response));

    public Task NotifyMessageRemovedAsync(MessageRemovedResponse response) =>
        TryNotifyAsync(() => NotifyParticipantsAsync(response.RecipientId,
            response.RecipientType, ChatHubConstants.MessageRemoved, response));

    public Task NotifyMessageEditedAsync(MessageEditResponse response) =>
        TryNotifyAsync(() => NotifyParticipantsAsync(response.RecipientId,
            response.RecipientType, ChatHubConstants.MessageEdited, response));

    public Task NotifyGroupProfileChangedAsync(Guid groupId) =>
        TryNotifyAsync(() => NotifyParticipantsAsync(groupId, RecipientType.Group,
            ChatHubConstants.GroupProfileChanged, new GroupProfileChangedResponse { GroupId = groupId }));

    public Task NotifyGroupMemberChangedAsync(Guid groupId, Guid userId) => TryNotifyAsync(async () =>
    {
        var member = await _context.GroupMemberships.AsNoTracking()
            .Where(m => m.GroupId == groupId && m.UserId == userId)
            .Select(m => new
            {
                m.IsBanned,
                IsOwner = _context.ChatGroups.Any(g => g.Id == groupId && g.OwnerUserId == userId),
                IsAdmin = _context.GroupAdmins.Any(a => a.GroupId == groupId && a.UserId == userId)
            }).FirstOrDefaultAsync();
        var response = new GroupMemberChangedResponse
        {
            GroupId = groupId, UserId = userId,
            Status = member is null ? GroupMemberStatus.Left : member.IsBanned ? GroupMemberStatus.Banned : GroupMemberStatus.Active,
            Role = member is null || member.IsBanned ? null : member.IsOwner ? GroupRole.Owner : member.IsAdmin ? GroupRole.Admin : GroupRole.Member
        };
        // The affected user must learn about a ban/leave even after losing membership.
        // Only this membership event includes them; profile and chat events do not.
        await NotifyParticipantsAsync(groupId, RecipientType.Group, ChatHubConstants.GroupMemberChanged, response, userId);
    });

    public Task NotifyUserJoinedGroupsAsync(Guid userId) => TryNotifyAsync(async () =>
    {
        var groupIds = await _context.GroupMemberships.AsNoTracking()
            .Where(m => m.UserId == userId && !m.IsBanned).Select(m => m.GroupId).ToListAsync();
        foreach (var groupId in groupIds)
            await NotifyGroupMemberChangedAsync(groupId, userId);
    });

    private async Task NotifyParticipantsAsync(Guid chatId, RecipientType type, string method, object payload, Guid? affectedUserId = null)
    {
        // Resolve current participants instead of treating a chat ID as a user ID.
        // Deliver only to active sessions, including all of a participant's devices.
        List<Guid> participants;
        if (type == RecipientType.User)
        {
            var result = await _chats.GetPrivateChatAsync(chatId);
            if (result.IsFailure)
                return;
            participants = new[] { result.Value.UserAId, result.Value.UserBId }.Distinct().ToList();
        }
        else
            participants = await _context.GroupMemberships.AsNoTracking()
                .Where(m => m.GroupId == chatId && !m.IsBanned).Select(m => m.UserId).Distinct().ToListAsync();

        if (affectedUserId.HasValue)
            participants.Add(affectedUserId.Value);
        if (participants.Count > 0)
            await NotifyUsersAsync(participants, method, payload);
    }

    private async Task NotifyUsersAsync(IEnumerable<Guid> users, string method, object payload)
    {
        var now = DateTime.UtcNow;
        var sessions = await _context.UserSessions.AsNoTracking()
            .Where(s => users.Contains(s.UserId) && !s.IsRevoked && s.ExpiresAt > now)
            .Select(s => s.Id).Distinct().ToListAsync();
        if (sessions.Count > 0)
            await _hubContext.Clients.Groups(sessions.Select(ChatHubConstants.GetSessionGroup).ToList())
                .SendAsync(method, payload);
    }

    private async Task TryNotifyAsync(Func<Task> notify)
    {
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deliver chat notification; clients can resync through HTTP.");
        }
    }
}
