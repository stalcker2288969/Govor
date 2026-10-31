using Govor.Domain.Models.Messages;

namespace Govor.Contracts.Responses;

public class MessageResponse
{
    public Guid Id { get; set; }
    public Guid SenderId { get; set; }
    public Guid RecipientId { get; set; } // or GroupId
    public RecipientType RecipientType { get; set; }
    public string EncryptedContent { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public bool IsEdited { get; set; } = false;
    public DateTime? EditedAt { get; set; }
    public Guid? ReplyToMessageId { get; set; }
    public Guid? ForwardedFromMessageId { get; set; }
    public Guid? ForwardedFromUserId { get; set; }
    public DateTime? ForwardedFromSentAt { get; set; }
    public long ReactionsVersion { get; set; }
    
    public List<MediaAttachmentResponse> MediaAttachments { get; set; } = new();
    public List<MessageReactionResponse> Reactions { get; set; } = new();
    public List<MessageViewResponse> MessageViews { get; set; } = new();
}
