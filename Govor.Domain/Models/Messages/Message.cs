namespace Govor.Domain.Models.Messages;

public class Message
{
    public Guid Id { get; set; }
    public Guid SenderId { get; set; }
    public Guid RecipientId { get; set; } // or GroupId
    public RecipientType RecipientType { get; set; }
    public string EncryptedContent { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public bool IsEdited { get; set; } = false;
    public DateTime? EditedAt { get; set; }
    public List<MessageReaction> Reactions { get; set; } = new List<MessageReaction>();
    public List<MediaAttachments> MediaAttachments { get; set; } = new List<MediaAttachments>();
    public List<MessageView> MessageViews { get; set; } = new List<MessageView>();
    
    public Guid? ReplyToMessageId { get; set; }
    // Attribution is a snapshot: deleting the source must not delete the forwarded message.
    public Guid? ForwardedFromMessageId { get; set; }
    public Guid? ForwardedFromUserId { get; set; }
    public DateTime? ForwardedFromSentAt { get; set; }
    public long ReactionsVersion { get; set; }
    
    public override bool Equals(object? obj)
    {
        if (obj is not Message other) return false;

        return Id == other.Id &&
               SenderId == other.SenderId &&
               RecipientId == other.RecipientId &&
               RecipientType == other.RecipientType &&
               EncryptedContent == other.EncryptedContent &&
               SentAt == other.SentAt &&
               IsEdited == other.IsEdited &&
               EditedAt == other.EditedAt &&
               ReplyToMessageId == other.ReplyToMessageId &&
               ForwardedFromMessageId == other.ForwardedFromMessageId &&
               ForwardedFromUserId == other.ForwardedFromUserId &&
               ForwardedFromSentAt == other.ForwardedFromSentAt;
    }
}

public enum RecipientType
{
    User,
    Group
}
