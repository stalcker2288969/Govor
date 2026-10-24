using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jose;
using AesGcm = System.Security.Cryptography.AesGcm;

namespace Govor.Application.E2ee;

// Shared verbatim with the mobile client. JOSE implements RFC 7515/7516;
// the application envelope binds the ciphertext to its author and conversation.
public sealed record DeviceKey(Guid UserId, Guid SessionId, string IdentityKey,
    string EncryptionKey, string Signature)
{
    public string Fingerprint => E2eeProtocol.Fingerprint(IdentityKey, EncryptionKey);
}

public sealed record ConversationKeys(Guid ConversationId, int RecipientType,
    Guid[] Members, DeviceKey[] Devices);
public sealed record AttachmentSecret(Guid MediaId, string Key, string FileName, string MimeType);
public sealed record MessagePlaintext(string Text, AttachmentSecret[] Attachments);
public sealed record EncryptedMessage(int Version, Guid MessageId, Guid ConversationId,
    int RecipientType, DeviceKey Sender, Guid? ReplyToMessageId, long CreatedAt,
    bool IsEdit, string[] Recipients, Guid[] MediaIds, string Ciphertext);
public sealed record DevicePrivateKeys(string IdentityKey, string EncryptionKey);

public static class E2eeProtocol
{
    public const string Prefix = "govor.e2ee.v1:";
    public const int MaxEnvelopeLength = 1_000_000;
    public const int MaxDevices = 500;
    public const string EncryptedMimeType = "application/vnd.govor.encrypted";
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static bool IsEncrypted(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static DevicePrivateKeys GenerateKeys()
    {
        using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var encryption = RSA.Create(3072);
        return new(Convert.ToBase64String(signing.ExportPkcs8PrivateKey()),
            Convert.ToBase64String(encryption.ExportPkcs8PrivateKey()));
    }

    public static DeviceKey PublicKey(DevicePrivateKeys keys, Guid user, Guid session)
    {
        using var signing = SigningPrivateKey(keys);
        using var encryption = EncryptionPrivateKey(keys);
        var identity = Convert.ToBase64String(signing.ExportSubjectPublicKeyInfo());
        var publicEncryption = Convert.ToBase64String(encryption.ExportSubjectPublicKeyInfo());
        // Domain separation prevents using this signature as a message signature.
        var signature = signing.SignData(KeyBinding(publicEncryption), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new(user, session, identity, publicEncryption, Convert.ToBase64String(signature));
    }

    public static void ValidatePublicKey(DeviceKey key)
    {
        if (key.UserId == Guid.Empty || key.SessionId == Guid.Empty ||
            key.IdentityKey is null || key.IdentityKey.Length > 200 ||
            key.EncryptionKey is null || key.EncryptionKey.Length > 1000 ||
            key.Signature is null || key.Signature.Length > 100)
            throw new CryptographicException("Invalid E2EE device key.");
        using var signing = SigningPublicKey(key);
        using var encryption = EncryptionPublicKey(key);
        if (signing.KeySize != 256 || signing.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
            encryption.KeySize != 3072 ||
            !signing.VerifyData(KeyBinding(key.EncryptionKey), Convert.FromBase64String(key.Signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Invalid E2EE key binding signature.");
    }

    public static string Fingerprint(string identity, string encryption) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"govor-device-v1\n{identity}\n{encryption}")));

    public static string Encrypt(MessagePlaintext plaintext, DevicePrivateKeys privateKeys,
        DeviceKey sender, ConversationKeys conversation, Guid messageId, Guid? replyTo = null,
        bool isEdit = false, long? createdAt = null)
    {
        ValidatePublicKey(sender);
        if (messageId == Guid.Empty || conversation.Devices.Length is 0 or > MaxDevices ||
            plaintext.Text is null || plaintext.Text.Length > 20_000 || plaintext.Attachments is null ||
            plaintext.Attachments.Length > 100 ||
            (string.IsNullOrWhiteSpace(plaintext.Text) && plaintext.Attachments.Length == 0))
            throw new CryptographicException("E2EE message or recipient limit exceeded.");
        var devices = conversation.Devices.DistinctBy(k => k.Fingerprint).ToArray();
        var keys = new List<RSA>();
        try
        {
            var recipients = devices.Select(device =>
            {
                ValidatePublicKey(device);
                var rsa = EncryptionPublicKey(device);
                keys.Add(rsa);
                return new JweRecipient(JweAlgorithm.RSA_OAEP_256, rsa,
                    new Dictionary<string, object> { ["kid"] = device.Fingerprint });
            }).ToArray();
            var ciphertext = JWE.Encrypt(JsonSerializer.Serialize(plaintext), recipients, JweEncryption.A256GCM);
            var envelope = new EncryptedMessage(1, messageId, conversation.ConversationId,
                conversation.RecipientType, sender, replyTo, createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                isEdit, devices.Select(k => k.Fingerprint).Order(StringComparer.Ordinal).ToArray(),
                plaintext.Attachments.Select(a => a.MediaId).Order().ToArray(), ciphertext);
            using var signing = SigningPrivateKey(privateKeys);
            var result = Prefix + JWT.Encode(JsonSerializer.Serialize(envelope), signing, JwsAlgorithm.ES256);
            if (result.Length > MaxEnvelopeLength) throw new CryptographicException("E2EE envelope is too large.");
            return result;
        }
        finally { foreach (var key in keys) key.Dispose(); }
    }

    // Reading the unverified payload is used only to select its verification key.
    // No caller may trust the returned fields until Verify has completed.
    public static EncryptedMessage Inspect(string value)
    {
        if (!IsEncrypted(value) || value.Length > MaxEnvelopeLength)
            throw new CryptographicException("An E2EE envelope is required.");
        var parts = value[Prefix.Length..].Split('.');
        if (parts.Length != 3) throw new CryptographicException("Invalid signed E2EE envelope.");
        var envelope = JsonSerializer.Deserialize<EncryptedMessage>(Base64Url.Decode(parts[1]), Json)
            ?? throw new CryptographicException("Invalid E2EE envelope.");
        if (envelope.Version != 1 || envelope.Sender is null || envelope.MessageId == Guid.Empty ||
            envelope.ConversationId == Guid.Empty || envelope.RecipientType is < 0 or > 1 ||
            envelope.Recipients is null || envelope.Recipients.Length is 0 or > MaxDevices ||
            envelope.MediaIds is null || envelope.MediaIds.Length > 100 ||
            string.IsNullOrWhiteSpace(envelope.Ciphertext))
            throw new CryptographicException("Invalid E2EE envelope fields.");
        return envelope;
    }

    public static EncryptedMessage Verify(string value)
    {
        var envelope = Inspect(value);
        ValidatePublicKey(envelope.Sender);
        using var signing = SigningPublicKey(envelope.Sender);
        JWT.Decode(value[Prefix.Length..], signing, JwsAlgorithm.ES256);
        // Reject algorithm negotiation, compression, and unprotected shared headers.
        using var jwe = JsonDocument.Parse(envelope.Ciphertext);
        var root = jwe.RootElement;
        if (root.TryGetProperty("unprotected", out _) || root.TryGetProperty("aad", out _))
            throw new CryptographicException("Unsupported JWE headers.");
        using var header = JsonDocument.Parse(Base64Url.Decode(root.GetProperty("protected").GetString()));
        if (header.RootElement.GetProperty("enc").GetString() != "A256GCM" ||
            header.RootElement.TryGetProperty("zip", out _))
            throw new CryptographicException("Unsupported content encryption.");
        var recipients = root.TryGetProperty("recipients", out var many)
            ? many.EnumerateArray().ToArray() : new[] { root };
        if (recipients.Length != envelope.Recipients.Length ||
            recipients.Any(r => r.GetProperty("header").GetProperty("alg").GetString() != "RSA-OAEP-256") ||
            !recipients.Select(r => r.GetProperty("header").GetProperty("kid").GetString())
                .Order(StringComparer.Ordinal).SequenceEqual(envelope.Recipients.Order(StringComparer.Ordinal)) ||
            envelope.Recipients.Distinct(StringComparer.Ordinal).Count() != envelope.Recipients.Length)
            throw new CryptographicException("Invalid E2EE recipient manifest.");
        return envelope;
    }

    public static MessagePlaintext Decrypt(string value, DevicePrivateKeys keys, Guid expectedMessage,
        Guid expectedConversation, int expectedType, Guid expectedAuthor, Guid? expectedReply = null,
        bool? expectedEdit = null)
    {
        var envelope = Verify(value);
        if (envelope.MessageId != expectedMessage || envelope.ConversationId != expectedConversation ||
            envelope.RecipientType != expectedType || envelope.Sender.UserId != expectedAuthor ||
            envelope.ReplyToMessageId != expectedReply ||
            (expectedEdit.HasValue && envelope.IsEdit != expectedEdit))
            throw new CryptographicException("E2EE message context does not match.");
        using var rsa = EncryptionPrivateKey(keys);
        using var signing = SigningPrivateKey(keys);
        var fingerprint = Fingerprint(Convert.ToBase64String(signing.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
        if (!envelope.Recipients.Contains(fingerprint, StringComparer.Ordinal))
            throw new CryptographicException("This device cannot decrypt this message.");
        // Select our recipient explicitly. In particular Windows CNG can throw an
        // OS-specific error while trying another recipient's RSA-OAEP encrypted key.
        var selected = System.Text.Json.Nodes.JsonNode.Parse(envelope.Ciphertext)!;
        if (selected["recipients"] is System.Text.Json.Nodes.JsonArray recipients)
        {
            var recipient = recipients.Single(r => r!["header"]!["kid"]!.GetValue<string>() == fingerprint)!;
            selected["recipients"] = new System.Text.Json.Nodes.JsonArray(recipient.DeepClone());
        }
        var decrypted = JWE.Decrypt(selected.ToJsonString(), rsa,
            JweAlgorithm.RSA_OAEP_256, JweEncryption.A256GCM);
        var plaintext = JsonSerializer.Deserialize<MessagePlaintext>(decrypted.Plaintext, Json)
            ?? throw new CryptographicException("Invalid encrypted content.");
        if (plaintext.Text is null || plaintext.Text.Length > 20_000 || plaintext.Attachments is null ||
            !plaintext.Attachments.Select(a => a.MediaId).Order().SequenceEqual(envelope.MediaIds.Order()))
            throw new CryptographicException("Invalid encrypted attachment manifest.");
        return plaintext;
    }

    public static (byte[] Data, string Key) EncryptAttachment(byte[] plaintext)
    {
        if (plaintext.Length > 19_000_000) throw new CryptographicException("Attachment is too large.");
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var data = new byte[12 + plaintext.Length + 16];
            RandomNumberGenerator.Fill(data.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(data.AsSpan(0, 12), plaintext, data.AsSpan(12, plaintext.Length), data.AsSpan(12 + plaintext.Length, 16),
                "govor-attachment-v1"u8);
            return (data, Convert.ToBase64String(key));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] DecryptAttachment(byte[] ciphertext, string encodedKey)
    {
        if (ciphertext.Length is < 28 or > 19_000_028) throw new CryptographicException("Invalid attachment size.");
        var key = Convert.FromBase64String(encodedKey);
        try
        {
            if (key.Length != 32) throw new CryptographicException("Invalid attachment key.");
            var plaintext = new byte[ciphertext.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(12, plaintext.Length),
                ciphertext.AsSpan(12 + plaintext.Length, 16), plaintext, "govor-attachment-v1"u8);
            return plaintext;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] KeyBinding(string encryption) => Encoding.UTF8.GetBytes("govor-key-binding-v1\n" + encryption);
    private static ECDsa SigningPrivateKey(DevicePrivateKeys keys)
    {
        var bytes = Convert.FromBase64String(keys.IdentityKey);
        var key = ECDsa.Create();
        try { key.ImportPkcs8PrivateKey(bytes, out _); return key; }
        catch { key.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static RSA EncryptionPrivateKey(DevicePrivateKeys keys)
    {
        var bytes = Convert.FromBase64String(keys.EncryptionKey);
        var key = RSA.Create();
        try { key.ImportPkcs8PrivateKey(bytes, out _); return key; }
        catch { key.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static ECDsa SigningPublicKey(DeviceKey device)
    {
        var key = ECDsa.Create();
        try
        {
            var bytes = Convert.FromBase64String(device.IdentityKey);
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length) throw new CryptographicException("Trailing public key data.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }
    private static RSA EncryptionPublicKey(DeviceKey device)
    {
        var key = RSA.Create();
        try
        {
            var bytes = Convert.FromBase64String(device.EncryptionKey);
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length) throw new CryptographicException("Trailing public key data.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }
}
