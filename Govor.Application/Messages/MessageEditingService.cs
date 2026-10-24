using Govor.Application.Messages.Parameters;
using Microsoft.EntityFrameworkCore;
using Govor.Domain;
using Govor.Domain.Models.Messages;
using Govor.Application.Groups;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Govor.Application.E2ee;

namespace Govor.Application.Messages;

public class MessageEditingService : IMessageEditingService
{
     private readonly GovorDbContext _dbContext;
    private readonly ILogger<MessageEditingService> _logger;
    private readonly MessageEditingOptions _options;

    public MessageEditingService(
        GovorDbContext dbContext,
        ILogger<MessageEditingService> logger,
        IOptions<MessageEditingOptions> options)
    {
        _dbContext = dbContext;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<EditMessageResult> EditMessageAsync(EditMessage editParams)
    {
        var target = await _dbContext.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == editParams.MessageId);
        if (target?.RecipientType != RecipientType.Group) return await EditCoreAsync(editParams);
        return await _dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                await _dbContext.ChatGroups.Where(g => g.Id == target.RecipientId)
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.Name, g => g.Name));
                var result = await EditCoreAsync(editParams);
                if (result.IsSuccess) await transaction.CommitAsync();
                return result;
            }
            catch { _dbContext.ChangeTracker.Clear(); throw; }
        });
    }

    private async Task<EditMessageResult> EditCoreAsync(EditMessage editParams)
    {
        var message = await _dbContext.Messages
            .Include(m => m.MediaAttachments)
            .FirstOrDefaultAsync(m => m.Id == editParams.MessageId);

        if (message == null)
        {
            return new EditMessageResult(
                false,
                new KeyNotFoundException("Message not found."),
                null);
        }

        if (message.SenderId != editParams.EditorId || !await _dbContext.HasChatAccessAsync(
            editParams.EditorId, message.RecipientId, message.RecipientType) ||
            (message.RecipientType == RecipientType.Group &&
             await _dbContext.ChatGroups.AnyAsync(g => g.Id == message.RecipientId && g.IsChannel) &&
             !await _dbContext.IsGroupAdministratorAsync(message.RecipientId, editParams.EditorId)))
        {
            _logger.LogWarning(
                "User {EditorId} unauthorized to edit message {MessageId}",
                editParams.EditorId,
                editParams.MessageId);

            return new EditMessageResult(
                false,
                new UnauthorizedAccessException(
                    "User is not authorized to edit this message."),
                null);
        }

        // Проверяем время, прошедшее с момента отправки
        var now = editParams.EditedAt;
        var editDeadline = message.SentAt.AddMinutes(
            _options.MaxEditTimeMinutes);

        if (now > editDeadline && _options.Enabled)
        {
            _logger.LogWarning(
                "Message {MessageId} cannot be edited. " +
                "Edit time limit of {MaxEditTimeMinutes} minutes has expired.",
                message.Id,
                _options.MaxEditTimeMinutes);

            return new EditMessageResult(
                false,
                new InvalidOperationException(
                    $"Message can only be edited within " +
                    $"{_options.MaxEditTimeMinutes} minutes after sending."),
                null);
        }

        var originalMessageSnapshot = new Message
        {
            Id = message.Id,
            SenderId = message.SenderId,
            RecipientId = message.RecipientId,
            RecipientType = message.RecipientType,
            SentAt = message.SentAt,
            ReplyToMessageId = message.ReplyToMessageId,
            MediaAttachments = message.MediaAttachments?.ToList() ?? []
        };

        if (editParams.NewContent is null || editParams.NewContent.Length > E2eeProtocol.MaxEnvelopeLength ||
            (string.IsNullOrWhiteSpace(editParams.NewContent) && message.MediaAttachments.Count == 0))
            return new EditMessageResult(false, new ArgumentException("Invalid message content."), null);

        try
        {
            var envelope = await new E2eeService(_dbContext).ValidateAsync(editParams.NewContent,
                editParams.EditorId, message.RecipientId, message.RecipientType, message.ReplyToMessageId,
                message.MediaAttachments.Select(a => a.MediaFileId).ToArray(), message.Id);
            if (E2eeProtocol.IsEncrypted(message.EncryptedContent) &&
                (envelope is null || envelope.CreatedAt <= E2eeProtocol.Inspect(message.EncryptedContent).CreatedAt))
                throw new ArgumentException("An encrypted message cannot be downgraded or replayed.");
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        { return new EditMessageResult(false, ex, null); }

        message.EncryptedContent = editParams.NewContent;
        message.IsEdited = true;
        message.EditedAt = editParams.EditedAt;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Message {MessageId} edited successfully.",
            editParams.MessageId);

        return new EditMessageResult(
            true,
            null,
            originalMessageSnapshot);
    }
}
