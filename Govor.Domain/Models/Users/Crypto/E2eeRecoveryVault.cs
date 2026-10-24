namespace Govor.Domain.Models.Users.Crypto;

public sealed class E2eeRecoveryVault
{
    public Guid UserId { get; set; }
    public long Revision { get; set; }
    public string IdentityKey { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public string Signature { get; set; } = "";
    public long Epoch { get; set; }
    public long PasswordCredentialVersion { get; set; }
    public string? PasswordCiphertext { get; set; }
    public string? PasswordSignature { get; set; }
}
