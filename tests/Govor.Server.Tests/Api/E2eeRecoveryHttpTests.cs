using System.Net;
using System.Net.Http.Json;
using Govor.Application.E2ee;
using Govor.Domain.Models.Messages;

namespace Govor.Server.Tests;

public partial class ReadApiTests
{
    [Test]
    public async Task RecoveryHttpEndpointsAuthenticateScopeArchivesAndValidateCertificates()
    {
        Assert.That((await _client.GetAsync("/api/e2ee/recovery")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize();
        Assert.That((await _client.GetFromJsonAsync<RecoveryState>("/api/e2ee/recovery"))!.Vault, Is.Null);
        var identity = E2eeRecoveryProtocol.GenerateIdentity();
        var code = E2eeRecoveryProtocol.GenerateCode();
        var vault = E2eeRecoveryProtocol.Seal(_db.Bob, 1, identity, code, "history secret");
        var request = new SaveRecoveryVault(0, vault.IdentityKey, vault.Ciphertext, vault.Signature);
        Assert.That((await _client.PutAsJsonAsync("/api/e2ee/recovery", request)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PutAsJsonAsync("/api/e2ee/recovery", request)).StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await _client.PutAsJsonAsync("/api/e2ee/recovery", request with { Signature = "AAAA" })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var restored = (await _client.GetFromJsonAsync<RecoveryState>("/api/e2ee/recovery"))!.Vault!;
        Assert.That(E2eeRecoveryProtocol.Open(restored, _db.Bob, code), Is.EqualTo("history secret"));
        var device = E2eeRecoveryProtocol.Certify(E2eeProtocol.PublicKey(E2eeProtocol.GenerateKeys(), _db.Bob, _session), identity);
        Assert.That((await _client.PutAsJsonAsync("/api/e2ee/device", device)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var own = await _client.GetFromJsonAsync<DeviceKey[]>("/api/e2ee/devices");
        Assert.That(own, Has.Length.EqualTo(1));
        Assert.That((await _client.PutAsJsonAsync("/api/e2ee/devices/certificate", device)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PutAsJsonAsync("/api/e2ee/devices/certificate", device with { UserId = _db.Alice })).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        _db.Context.Messages.Add(new Message { Id = Guid.NewGuid(), SenderId = _db.Bob, RecipientId = _db.ChatId,
            RecipientType = RecipientType.User, EncryptedContent = "legacy", SentAt = DateTime.UtcNow });
        await _db.Context.SaveChangesAsync();
        var signer = await _client.GetFromJsonAsync<DeviceKey>($"/api/e2ee/conversations/0/{_db.ChatId}/signers/{_session}");
        Assert.That(signer!.AccountIdentityKey, Is.EqualTo(device.AccountIdentityKey));
        Assert.That((await _client.GetAsync($"/api/e2ee/conversations/0/{Guid.NewGuid()}/signers/{_session}")).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
    }
}
