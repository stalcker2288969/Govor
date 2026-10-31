using Govor.Domain.Models;
using Govor.Domain.Models.Messages;

namespace Govor.Contracts.Responses.SignalR;

public class UserMessageResponse
{
    public Guid MessageId { get; set; }
    public Guid SenderId { get; set; }
    public Guid RecipientId { get; set; }
    public RecipientType RecipientType{get; set; }
    public string EncryptedContent { get; set; } = string.Empty;
    public Guid? ReplyToMessageId { get; set; }
    public Guid? ForwardedFromMessageId { get; set; }
    public Guid? ForwardedFromUserId { get; set; }
    public DateTime? ForwardedFromSentAt { get; set; }
    public DateTime SentAt { get; set; }
    public bool IsEdited { get; set; } = false;
    public List<MediaFile> MediaAttachments { get; set; } = new List<MediaFile>();
}
