namespace Govor.Domain.Models.Users.Crypto;

public sealed class E2eeRecoveryVault
{
    public Guid UserId { get; set; }
    public long Revision { get; set; }
    public string IdentityKey { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public string Signature { get; set; } = "";
}
