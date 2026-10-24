namespace Govor.Domain.Models.Users.Crypto;

public sealed class E2eeIdentityChange
{
    public Guid UserId { get; set; }
    public long Epoch { get; set; }
    public string PreviousIdentityKey { get; set; } = "";
    public string IdentityKey { get; set; } = "";
    public long ChangedAt { get; set; }
    public Guid? PreviousSessionId { get; set; }
    public string? PreviousDeviceKeyJson { get; set; }
}
