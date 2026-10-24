using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jose;

namespace Govor.Application.E2ee;

public sealed record RecoveryVault(Guid UserId, long Revision, string IdentityKey, string Ciphertext, string Signature);
public sealed record RecoveryState(RecoveryVault? Vault);
public sealed record SaveRecoveryVault(long ExpectedRevision, string IdentityKey, string Ciphertext, string Signature);
public sealed record HistoryKey(string IdentityKey, string EncryptionKey)
{
    private string? _fingerprint;
    [JsonIgnore]
    public string Fingerprint
    {
        get
        {
            if (_fingerprint is not null) return _fingerprint;
            using var rsa = RSA.Create();
            var bytes = Convert.FromBase64String(EncryptionKey);
            try { rsa.ImportPkcs8PrivateKey(bytes, out _); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return _fingerprint = E2eeProtocol.Fingerprint(IdentityKey, Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
        }
    }
}

// Recovery is a separate security domain from message encryption. Possession of
// the random 256-bit recovery code authorizes enrollment and archive recovery.
public static class E2eeRecoveryProtocol
{
    public const int MaxVaultLength = 4_000_000;
    public static string GenerateCode() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    public static string GenerateIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportPkcs8PrivateKey());
    }

    public static string PublicIdentity(string privateKey)
    {
        using var key = PrivateIdentity(privateKey);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public static string AccountFingerprint(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("govor-account-v1\n" + identity)));

    public static DeviceKey Certify(DeviceKey device, string privateIdentity, long generation = 0,
        string? previousFingerprint = null)
    {
        var certified = device with { AccountIdentityKey = PublicIdentity(privateIdentity),
            Generation = generation, PreviousFingerprint = previousFingerprint };
        using var key = PrivateIdentity(privateIdentity);
        return certified with { AccountSignature = Sign(key, DeviceBinding(certified)) };
    }

    public static void VerifyCertificate(DeviceKey device)
    {
        if (device.AccountIdentityKey is null || device.AccountSignature is null || device.Generation < 0 ||
            (device.Generation == 0 ? device.PreviousFingerprint is not null : device.PreviousFingerprint?.Length != 64))
            throw new CryptographicException("Invalid account device certificate.");
        using var key = PublicIdentityKey(device.AccountIdentityKey);
        Verify(key, DeviceBinding(device), device.AccountSignature);
    }

    public static RecoveryVault Seal(Guid user, long revision, string privateIdentity, string code, string plaintext)
    {
        if (user == Guid.Empty || revision < 1) throw new CryptographicException("Invalid archive context.");
        var secret = CodeBytes(code);
        string encrypted;
        try { encrypted = JWT.Encode(new ArchivePayload(1, user, revision, plaintext), secret, JweAlgorithm.DIR, JweEncryption.A256GCM); }
        finally { CryptographicOperations.ZeroMemory(secret); }
        var vault = new RecoveryVault(user, revision, PublicIdentity(privateIdentity), encrypted, "");
        using var identity = PrivateIdentity(privateIdentity);
        return vault with { Signature = Sign(identity, VaultBinding(vault)) };
    }

    public static string Open(RecoveryVault vault, Guid user, string code)
    {
        Validate(vault);
        if (vault.UserId != user) throw new CryptographicException("Archive belongs to another account.");
        var secret = CodeBytes(code);
        try
        {
            var payload = JsonSerializer.Deserialize<ArchivePayload>(
                JWT.Decode(vault.Ciphertext, secret, JweAlgorithm.DIR, JweEncryption.A256GCM), E2eeProtocol.Json)
                ?? throw new CryptographicException("Invalid archive.");
            if (payload.Version != 1 || payload.UserId != user || payload.Revision != vault.Revision)
                throw new CryptographicException("Archive context mismatch.");
            return payload.Plaintext;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public static void Validate(RecoveryVault vault)
    {
        if (vault.UserId == Guid.Empty || vault.Revision < 1 || vault.Ciphertext is null ||
            vault.Ciphertext.Length is < 50 or > MaxVaultLength || vault.Signature is null)
            throw new CryptographicException("Invalid archive.");
        // Fix the algorithms even on the server, which cannot decrypt the archive.
        var parts = vault.Ciphertext.Split('.');
        if (parts.Length != 5 || parts[1].Length != 0) throw new CryptographicException("Invalid archive JWE.");
        using var header = JsonDocument.Parse(Base64Url.Decode(parts[0]));
        if (header.RootElement.EnumerateObject().Count() != 2 ||
            header.RootElement.GetProperty("alg").GetString() != "dir" ||
            header.RootElement.GetProperty("enc").GetString() != "A256GCM")
            throw new CryptographicException("Unsupported archive encryption.");
        using var key = PublicIdentityKey(vault.IdentityKey);
        Verify(key, VaultBinding(vault), vault.Signature);
    }

    private static byte[] CodeBytes(string code)
    {
        var bytes = Base64Url.Decode(code.Trim());
        if (bytes.Length == 32) return bytes;
        CryptographicOperations.ZeroMemory(bytes);
        throw new CryptographicException("Invalid recovery code.");
    }

    private static byte[] DeviceBinding(DeviceKey key) => JsonSerializer.SerializeToUtf8Bytes(new
    { purpose = "govor-account-device-v1", key.UserId, key.SessionId, key.Fingerprint, key.Generation, key.PreviousFingerprint });
    private static byte[] VaultBinding(RecoveryVault vault) => JsonSerializer.SerializeToUtf8Bytes(new
    { purpose = "govor-recovery-v1", vault.UserId, vault.Revision, vault.IdentityKey, vault.Ciphertext });
    private static string Sign(ECDsa key, byte[] bytes) => Convert.ToBase64String(key.SignData(bytes,
        HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    private static void Verify(ECDsa key, byte[] bytes, string signature)
    {
        if (signature.Length > 100 || !key.VerifyData(bytes, Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Invalid account signature.");
    }
    private static ECDsa PublicIdentityKey(string encoded)
    {
        if (encoded is null || encoded.Length > 200) throw new CryptographicException("Invalid account identity.");
        var key = ECDsa.Create();
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new CryptographicException("Invalid account identity curve.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }
    private static ECDsa PrivateIdentity(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        var key = ECDsa.Create();
        try { key.ImportPkcs8PrivateKey(bytes, out _); return key; }
        catch { key.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private sealed record ArchivePayload(int Version, Guid UserId, long Revision, string Plaintext);
}
