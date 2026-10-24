namespace Govor.Domain.Models.Users.Crypto;

public class UserCryptoSession
{
    public Guid Id { get; set; }

    public Guid UserSessionId { get; set; }
    public UserSession UserSession { get; set; }

    public byte[] PublicIdentityKey { get; set; }
    public string? AccountIdentityKey { get; set; }
    public string? AccountSignature { get; set; }
    public long Generation { get; set; }
    public string? PreviousFingerprint { get; set; }

    public SignedPreKey SignedPreKey { get; set; }
    public ICollection<OneTimePreKey> OneTimePreKeys { get; set; }
}
