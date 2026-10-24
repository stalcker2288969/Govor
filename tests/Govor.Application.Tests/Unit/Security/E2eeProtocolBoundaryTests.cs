using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Govor.Application.E2ee;
using Jose;

namespace Govor.Application.Tests.Unit.Security;

[TestFixture]
public class E2eeProtocolBoundaryTests
{
    private DevicePrivateKeys _keys = null!;
    private DeviceKey _device = null!;
    private ConversationKeys _conversation = null!;
    private Guid _message;
    [OneTimeSetUp]
    public void SetUp()
    {
        _keys = E2eeProtocol.GenerateKeys();
        _device = E2eeProtocol.PublicKey(_keys, Guid.NewGuid(), Guid.NewGuid());
        _conversation = new(Guid.NewGuid(), 0, new[] { _device.UserId }, new[] { _device });
        _message = Guid.NewGuid();
    }

    private string Encrypt() => E2eeProtocol.Encrypt(new("secret", []), _keys, _device, _conversation, _message);
    private string Sign(EncryptedMessage envelope)
    {
        using var signing = ECDsa.Create();
        signing.ImportPkcs8PrivateKey(Convert.FromBase64String(_keys.IdentityKey), out _);
        return E2eeProtocol.Prefix + JWT.Encode(JsonSerializer.Serialize(envelope), signing, JwsAlgorithm.ES256);
    }

    [TestCase("plaintext")]
    [TestCase("too-large")]
    [TestCase("segments")]
    [TestCase("null-json")]
    public void InspectRejectsInvalidEnvelopeBeforeTrustingPayload(string reason)
    {
        var input = reason switch {
            "plaintext" => "secret",
            "too-large" => E2eeProtocol.Prefix + new string('x', E2eeProtocol.MaxEnvelopeLength),
            "segments" => E2eeProtocol.Prefix + "only.two",
            _ => E2eeProtocol.Prefix + "header." + Base64Url.Encode("null"u8.ToArray()) + ".signature"
        };
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Inspect(input));
    }

    [TestCase("version")]
    [TestCase("sender")]
    [TestCase("message")]
    [TestCase("conversation")]
    [TestCase("type")]
    [TestCase("recipients")]
    [TestCase("media")]
    [TestCase("ciphertext")]
    public void InspectRejectsInvalidRoutingFields(string field)
    {
        var envelope = E2eeProtocol.Inspect(Encrypt());
        envelope = field switch {
            "version" => envelope with { Version = 2 },
            "sender" => envelope with { Sender = null! },
            "message" => envelope with { MessageId = Guid.Empty },
            "conversation" => envelope with { ConversationId = Guid.Empty },
            "type" => envelope with { RecipientType = 2 },
            "recipients" => envelope with { Recipients = [] },
            "media" => envelope with { MediaIds = null! },
            _ => envelope with { Ciphertext = "" }
        };
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Inspect(Sign(envelope)));
    }

    [TestCase("empty-message")]
    [TestCase("empty-content")]
    [TestCase("long-text")]
    [TestCase("null-attachments")]
    [TestCase("many-attachments")]
    [TestCase("no-devices")]
    [TestCase("many-devices")]
    public void EncryptEnforcesContentAndRecipientLimits(string reason)
    {
        var plaintext = reason switch {
            "empty-content" => new MessagePlaintext(" ", []),
            "long-text" => new MessagePlaintext(new string('x', 20_001), []),
            "null-attachments" => new MessagePlaintext("secret", null!),
            "many-attachments" => new MessagePlaintext("secret", Enumerable.Range(0, 101)
                .Select(_ => new AttachmentSecret(Guid.NewGuid(), "key", "file", "type")).ToArray()),
            _ => new MessagePlaintext("secret", [])
        };
        var directory = reason == "no-devices" ? _conversation with { Devices = [] } :
            reason == "many-devices" ? _conversation with { Devices = Enumerable.Repeat(_device, 501).ToArray() } : _conversation;
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Encrypt(plaintext, _keys, _device, directory,
            reason == "empty-message" ? Guid.Empty : _message));
    }

    [TestCase("unprotected")]
    [TestCase("aad")]
    [TestCase("compression")]
    [TestCase("encryption")]
    [TestCase("manifest")]
    [TestCase("algorithm")]
    public void ValidSignatureDoesNotAuthorizeUnsupportedEncryptionHeaders(string reason)
    {
        var envelope = E2eeProtocol.Inspect(Encrypt());
        var jwe = JsonNode.Parse(envelope.Ciphertext)!;
        if (reason is "unprotected" or "aad") jwe[reason] = reason == "aad" ? JsonValue.Create("value") : new JsonObject();
        if (reason is "compression" or "encryption")
        {
            var header = JsonNode.Parse(Base64Url.Decode(jwe["protected"]!.GetValue<string>()))!;
            if (reason == "compression") header["zip"] = "DEF"; else header["enc"] = "A128GCM";
            jwe["protected"] = Base64Url.Encode(Encoding.UTF8.GetBytes(header.ToJsonString()));
        }
        if (reason == "algorithm")
        {
            var recipient = jwe["recipients"] is JsonArray recipients ? recipients[0]! : jwe;
            recipient["header"]!["alg"] = "RSA1_5";
        }
        if (reason == "manifest") envelope = envelope with { Recipients = new[] { "wrong-fingerprint" } };
        var signed = Sign(envelope with { Ciphertext = jwe.ToJsonString() });
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Verify(signed));
    }

    [Test]
    public void RecipientTypeAndEditContextAreAuthenticated()
    {
        var encrypted = Encrypt();
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Decrypt(encrypted, _keys, _message,
            _conversation.ConversationId, 1, _device.UserId));
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Decrypt(encrypted, _keys, _message,
            _conversation.ConversationId, 0, _device.UserId, expectedEdit: true));
    }

    [TestCase("identity")]
    [TestCase("encryption")]
    [TestCase("trailing-identity")]
    [TestCase("trailing-encryption")]
    [TestCase("empty-user")]
    [TestCase("empty-session")]
    public void InvalidDevicePublicKeysAreRejected(string reason)
    {
        var bad = reason switch {
            "identity" => _device with { IdentityKey = "AAAA" },
            "encryption" => _device with { EncryptionKey = "AAAA" },
            "trailing-identity" => _device with { IdentityKey = Convert.ToBase64String(Convert.FromBase64String(_device.IdentityKey).Concat(new byte[] { 1 }).ToArray()) },
            "trailing-encryption" => _device with { EncryptionKey = Convert.ToBase64String(Convert.FromBase64String(_device.EncryptionKey).Concat(new byte[] { 1 }).ToArray()) },
            "empty-user" => _device with { UserId = Guid.Empty },
            _ => _device with { SessionId = Guid.Empty }
        };
        Assert.Throws<CryptographicException>(() => E2eeProtocol.ValidatePublicKey(bad));
    }

    [Test]
    public void InvalidPrivateKeysDoNotProducePublicKeys()
    {
        Assert.Throws<CryptographicException>(() => E2eeProtocol.PublicKey(_keys with { IdentityKey = "AAAA" }, _device.UserId, _device.SessionId));
        Assert.Throws<CryptographicException>(() => E2eeProtocol.PublicKey(_keys with { EncryptionKey = "AAAA" }, _device.UserId, _device.SessionId));
    }

    [TestCase("short")]
    [TestCase("large")]
    [TestCase("key")]
    public void AttachmentsEnforceCiphertextSizeAndKeyLength(string reason)
    {
        if (reason == "large") Assert.Throws<CryptographicException>(() => E2eeProtocol.EncryptAttachment(new byte[19_000_001]));
        else if (reason == "short") Assert.Throws<CryptographicException>(() => E2eeProtocol.DecryptAttachment(new byte[27], Convert.ToBase64String(new byte[32])));
        else
        {
            var (encrypted, _) = E2eeProtocol.EncryptAttachment(new byte[] { 1 });
            Assert.Throws<CryptographicException>(() => E2eeProtocol.DecryptAttachment(encrypted, Convert.ToBase64String(new byte[16])));
        }
    }

    [TestCase("null")]
    [TestCase("{\"Text\":null,\"Attachments\":[]}")]
    [TestCase("{\"Text\":\"secret\",\"Attachments\":[{\"MediaId\":\"11111111-1111-1111-1111-111111111111\"}]}")]
    public void DecryptionRejectsMalformedPlaintextOrAttachmentManifest(string plaintext)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(_device.EncryptionKey), out _);
        var ciphertext = JWE.Encrypt(plaintext, new[] { new JweRecipient(JweAlgorithm.RSA_OAEP_256, rsa,
            new Dictionary<string, object> { ["kid"] = _device.Fingerprint }) }, JweEncryption.A256GCM);
        var envelope = E2eeProtocol.Inspect(Encrypt()) with { Ciphertext = ciphertext };
        Assert.Throws<CryptographicException>(() => E2eeProtocol.Decrypt(Sign(envelope), _keys, _message,
            _conversation.ConversationId, 0, _device.UserId));
    }
}

