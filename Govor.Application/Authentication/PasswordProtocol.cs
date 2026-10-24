using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Numerics;
using Jose;
using SecureRemotePassword;
using Govor.Application.E2ee;

namespace Govor.Application.Authentication;

public sealed record PasswordCredentials(int Version, string Salt, string Identity, string Verifier);
public sealed record PasswordEnvelope(string IdentityKey, long CredentialVersion, string Ciphertext, string Signature);
public sealed record PasswordConfiguration(PasswordCredentials Credentials, long CredentialVersion, PasswordEnvelope? Envelope);
public sealed record PasswordChallengeRequest(string Name, string PublicEphemeral, string DeviceInfo = "");
public sealed record PasswordChallenge(Guid ChallengeId, string Salt, string Identity, string PublicEphemeral, long CredentialVersion, bool Legacy = false);
public sealed record PasswordProof(Guid ChallengeId, string Proof);
public sealed record PasswordLoginResult(string RefreshToken, string AccessToken, string ServerProof, long CredentialVersion);
public sealed record PasswordActionResult(Guid AuthorizationId, string ServerProof, long CredentialVersion);
public sealed record PasswordEnrollmentRequest(PasswordCredentials Credentials, string? LegacyPassword = null);
public sealed record SavePasswordEnvelope(PasswordEnvelope Envelope);
public sealed record ChangePasswordRequest(Guid AuthorizationId, PasswordCredentials Credentials, PasswordEnvelope? Envelope);
public sealed record ResetEncryptionRequest(Guid AuthorizationId, string ExpectedIdentityKey, long ExpectedRevision,
    RecoveryVault Vault, PasswordEnvelope Envelope);
public sealed record AccountIdentityChange(Guid UserId, long Epoch, string PreviousIdentityKey, string IdentityKey, long ChangedAt);

// SRP-6a is supplied by srp.net, rather than implemented here. Fixed parameters
// and separate HKDF domains prevent the authentication verifier/session key from
// being used as the key that opens the recovery archive.
public static class PasswordProtocol
{
    public const int Iterations = 600_000;
    public static SrpParameters Parameters() => SrpParameters.Create4096<SHA512>();
    public static SrpClient Client() => new(Parameters());
    public static SrpServer Server() => new(Parameters());

    public static (PasswordCredentials Credentials, string ArchiveKey) Create(string password)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var identity = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var (secret, archiveKey) = Derive(password, salt, identity);
        var client = Client();
        return (new(1, salt, identity, client.DeriveVerifier(client.DerivePrivateKey(salt, identity, secret))), archiveKey);
    }

    public static (string Secret, string ArchiveKey) Derive(string password, string salt, string identity)
    {
        if (password is null || password.Length is < 8 or > 256 || !Hex(salt, 64) || !Hex(identity, 64))
            throw new CryptographicException("Invalid password or password parameters.");
        var master = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromHexString(salt), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            var auth = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("govor-auth-v1\n" + identity));
            var archive = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("govor-password-archive-v1\n" + identity));
            try { return (Base64Url.Encode(auth), Base64Url.Encode(archive)); }
            finally { CryptographicOperations.ZeroMemory(auth); CryptographicOperations.ZeroMemory(archive); }
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    public static void Validate(PasswordCredentials credentials)
    {
        if (credentials is null || credentials.Version != 1 || !Hex(credentials.Salt, 64) ||
            !Hex(credentials.Identity, 64) || !Hex(credentials.Verifier, 1024) ||
            !SrpUtility.IsValidVerifier(Parameters(), credentials.Verifier) || !GroupElement(credentials.Verifier))
            throw new CryptographicException("Invalid SRP credentials.");
    }

    public static bool Hex(string? value, int length) => value?.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
    public static bool GroupElement(string? value)
    {
        if (!Hex(value, 1024)) return false;
        var integer = new BigInteger(Convert.FromHexString(value!), isUnsigned: true, isBigEndian: true);
        var prime = new BigInteger(Convert.FromHexString(Parameters().Prime.ToHex()), isUnsigned: true, isBigEndian: true);
        return integer > 1 && integer < prime - 1;
    }

    public static PasswordEnvelope Seal(Guid user, long version, PasswordCredentials credentials,
        string privateIdentity, string code, string archiveKey)
    {
        Validate(credentials);
        var root = E2eeRecoveryProtocol.PublicIdentity(privateIdentity);
        var key = Base64Url.Decode(archiveKey);
        try
        {
            if (key.Length != 32) throw new CryptographicException("Invalid archive key.");
            var ciphertext = Jose.JWT.Encode(new KeyPayload(1, user, root, version, code), key, JweAlgorithm.DIR, JweEncryption.A256GCM);
            var envelope = new PasswordEnvelope(root, version, ciphertext, "");
            return envelope with { Signature = E2eeRecoveryProtocol.SignAccountPayload(privateIdentity, Binding(user, credentials, envelope)) };
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static string Open(Guid user, PasswordCredentials credentials, PasswordEnvelope envelope, string archiveKey)
    {
        ValidateEnvelope(user, credentials, envelope);
        var key = Base64Url.Decode(archiveKey);
        try
        {
            if (key.Length != 32) throw new CryptographicException("Invalid archive key.");
            var payload = JsonSerializer.Deserialize<KeyPayload>(Jose.JWT.Decode(envelope.Ciphertext, key,
                JweAlgorithm.DIR, JweEncryption.A256GCM), E2eeProtocol.Json) ?? throw new CryptographicException("Invalid password archive.");
            if (payload.Version != 1 || payload.UserId != user || payload.IdentityKey != envelope.IdentityKey ||
                payload.CredentialVersion != envelope.CredentialVersion || Base64Url.Decode(payload.Code).Length != 32)
                throw new CryptographicException("Password archive context mismatch.");
            return payload.Code;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static void ValidateEnvelope(Guid user, PasswordCredentials credentials, PasswordEnvelope envelope)
    {
        Validate(credentials);
        if (user == Guid.Empty || envelope is null || envelope.CredentialVersion < 1 || envelope.Ciphertext is null ||
            envelope.Ciphertext.Length is < 50 or > 4096 || envelope.Signature is null)
            throw new CryptographicException("Invalid password archive.");
        var parts = envelope.Ciphertext.Split('.');
        if (parts.Length != 5 || parts[1].Length != 0) throw new CryptographicException("Invalid password archive JWE.");
        using var header = JsonDocument.Parse(Base64Url.Decode(parts[0]));
        if (header.RootElement.EnumerateObject().Count() != 2 || header.RootElement.GetProperty("alg").GetString() != "dir" ||
            header.RootElement.GetProperty("enc").GetString() != "A256GCM") throw new CryptographicException("Invalid archive algorithms.");
        E2eeRecoveryProtocol.VerifyAccountPayload(envelope.IdentityKey, Binding(user, credentials, envelope), envelope.Signature);
    }

    private static byte[] Binding(Guid user, PasswordCredentials credentials, PasswordEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(new { purpose = "govor-password-recovery-v1", user,
            credentials, envelope.IdentityKey, envelope.CredentialVersion, envelope.Ciphertext });
    private sealed record KeyPayload(int Version, Guid UserId, string IdentityKey, long CredentialVersion, string Code);
}
