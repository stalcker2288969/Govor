using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;

namespace Govor.Contracts.Requests;

public class MediaUploadRequest
{
    [Required]
    public IFormFile FromFile { get; set; }
    [Required]
    public MediaType Type { get; set; }
    [Required, MaxLength(128)]
    public string MimeType { get; set; } = string.Empty;
    public string? EncryptedKey { get; set; } = string.Empty;
    [Required]
    public MediaOwnerType OwnerType { get; set; } = MediaOwnerType.Message;
}
