using Govor.Domain.Models.Messages;

namespace Govor.Application.Messages.Parameters;

public record SendMessage(
    string EncryptContent, 
    Guid? ReplyToMessageId,
    Guid RecipientId,
    RecipientType RecipientType,
    Guid FromUserId,
    DateTime SendAt, 
    IEnumerable<SendMedia> Media,
    Guid? ForwardedFromMessageId = null);
