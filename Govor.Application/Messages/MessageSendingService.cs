using Govor.Application.Messages.Parameters;
using Microsoft.EntityFrameworkCore;
using Govor.Domain;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models;
using Govor.Application.Groups;
using Microsoft.Extensions.Logging;
using Govor.Application.E2ee;

namespace Govor.Application.Messages;

public class MessageSendingService : IMessageSendingService
{
    private readonly GovorDbContext _dbContext;
    private readonly ILogger<MessageSendingService> _logger;

    public MessageSendingService(GovorDbContext dbContext, ILogger<MessageSendingService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }
    
    public async Task<SendMessageResult> SendMessageAsync(SendMessage sendParams)
    {
        Guid operationId;
        try { operationId = E2eeProtocol.IsEncrypted(sendParams.EncryptContent)
            ? E2eeProtocol.Inspect(sendParams.EncryptContent).MessageId : Guid.NewGuid(); }
        catch { return new SendMessageResult(false, new ArgumentException("Invalid E2EE envelope."), default); }
        if (sendParams.RecipientType != RecipientType.Group)
            return await SendCoreAsync(sendParams, operationId);
        return await _dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                // Same group lock as moderation: bans/demotion cannot race a channel post.
                await _dbContext.ChatGroups.Where(g => g.Id == sendParams.RecipientId)
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.Name, g => g.Name));
                var result = await SendCoreAsync(sendParams, operationId);
                if (result.IsSuccess) await transaction.CommitAsync();
                return result;
            }
            catch { _dbContext.ChangeTracker.Clear(); throw; }
        });
    }

    private async Task<SendMessageResult> SendCoreAsync(SendMessage sendParams, Guid operationId)
    {
        var media = sendParams.Media?.ToArray() ?? [];
        if ((string.IsNullOrWhiteSpace(sendParams.EncryptContent) && media.Length == 0) ||
            (sendParams.EncryptContent?.Length ?? 0) > E2eeProtocol.MaxEnvelopeLength || media.Length > 100)
            return new SendMessageResult(false, new ArgumentException("Invalid message content or attachment count."), default);

        var validationResult = sendParams.RecipientType switch
        {
            RecipientType.User => await ValidateUserRecipientAsync(sendParams.FromUserId, sendParams.RecipientId),
            RecipientType.Group => await ValidateGroupRecipientAsync(sendParams.FromUserId, sendParams.RecipientId),
            _ => (Success: false, Error: "Invalid recipient type.")
        };

        if (!validationResult.Success)
        {
            _logger.LogWarning("Message send failed: {Error}", validationResult.Error);
            return new SendMessageResult(false, new UnauthorizedAccessException(validationResult.Error), default);
        }
        
        var completed = await _dbContext.Messages.AsNoTracking().Include(m => m.MediaAttachments)
            .ThenInclude(m => m.MediaFile).FirstOrDefaultAsync(m => m.Id == operationId);
        if (completed is not null)
            return completed.SenderId == sendParams.FromUserId && completed.EncryptedContent == sendParams.EncryptContent
                ? new SendMessageResult(true, null, completed)
                : new SendMessageResult(false, new ArgumentException("Message ID already exists."), default);

        var mediaIds = media.Select(m => m.MediaId).Distinct().ToArray();
        var files = await _dbContext.MediaFiles.Where(m => mediaIds.Contains(m.Id) &&
            m.UploaderId == sendParams.FromUserId && m.OwnerType == MediaOwnerType.Message).ToListAsync();
        if (files.Count != mediaIds.Length)
            return new SendMessageResult(false, new UnauthorizedAccessException("Invalid media ownership."), default);

        if (E2eeProtocol.IsEncrypted(sendParams.EncryptContent) &&
            (files.Any(f => f.MineType != E2eeProtocol.EncryptedMimeType) || media.Any(m => !string.IsNullOrEmpty(m.EncryptedKey))))
            return new SendMessageResult(false, new ArgumentException("E2EE attachments must be encrypted; keys belong inside the encrypted message."), default);

        if (sendParams.ReplyToMessageId.HasValue && !await _dbContext.Messages.AnyAsync(m =>
            m.Id == sendParams.ReplyToMessageId && m.RecipientId == sendParams.RecipientId &&
            m.RecipientType == sendParams.RecipientType))
            return new SendMessageResult(false, new ArgumentException("Reply must belong to the same chat."), default);

        try
        {
            await new E2eeService(_dbContext).ValidateAsync(sendParams.EncryptContent, sendParams.FromUserId,
                sendParams.RecipientId, sendParams.RecipientType, sendParams.ReplyToMessageId,
                media.Select(m => m.MediaId).ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        { return new SendMessageResult(false, ex, default); }

        var messageId = operationId;
        var message = new Message
        {
            Id = messageId,
            SenderId = sendParams.FromUserId,
            RecipientId = sendParams.RecipientId,
            RecipientType = sendParams.RecipientType,
            EncryptedContent = sendParams.EncryptContent ?? string.Empty,
            SentAt = sendParams.SendAt,
            IsEdited = false,
            ReplyToMessageId = sendParams.ReplyToMessageId,
            MediaAttachments = files.Select(file => new MediaAttachments
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                MediaFileId = file.Id,
                MediaFile = file
            }).ToList()
        };
        
        await _dbContext.Messages.AddAsync(message);
        // Save message and pending push atomically so a restart/provider outage cannot lose the push.
        _dbContext.ChatPushNotifications.Add(new ChatPushNotification
        { MessageId = messageId, NextAttemptAt = DateTime.UtcNow });
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Message {MessageId} sent successfully.", messageId);
        return new SendMessageResult(true, null, message);
    }

    private async Task<(bool Success, string Error)> ValidateUserRecipientAsync(Guid userId, Guid chatId)
    {
        var chat = await _dbContext.PrivateChats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId &&
            (c.UserAId == userId || c.UserBId == userId));
        if (chat is null)
            return (false, "You are not a member of this chat.");
        var blocked = await _dbContext.Friendships.AnyAsync(f => f.Status == FriendshipStatus.Blocked &&
            ((f.RequesterId == chat.UserAId && f.AddresseeId == chat.UserBId) ||
             (f.RequesterId == chat.UserBId && f.AddresseeId == chat.UserAId)));
        return blocked ? (false, "This conversation is blocked.") : (true, null);
    }

    private async Task<(bool Success, string Error)> ValidateGroupRecipientAsync(Guid userId, Guid groupId)
    {
        var group = await _dbContext.ChatGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId);
        if (group is null) return (false, "Group not found.");
        
        var isMember = await _dbContext.GroupMemberships.AnyAsync(gm => gm.UserId == userId && gm.GroupId == groupId && !gm.IsBanned);
        if (!isMember) return (false, "Sender is not a member of the group.");
        if (group.IsChannel && !await _dbContext.IsGroupAdministratorAsync(groupId, userId))
            return (false, "Only channel administrators can send messages.");

        return (true, null);
    }
}
