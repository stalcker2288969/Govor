using Govor.Domain.Models.Messages;
using System.ComponentModel.DataAnnotations;

namespace Govor.Contracts.Requests.SignalR;

public record MessageRequest
{
    public Guid RecipientId { get; init; }
    public RecipientType RecipientType { get; init; }
    [Required]
    [MaxLength(1_000_000, ErrorMessage = "EncryptedContent cannot exceed 1,000,000 characters.")]
    public string EncryptedContent { get; init; } = string.Empty;
    public Guid? ReplyToMessageId { get; set; }
    public List<MediaReference> MediaAttachments { get; set; } = new();
}
