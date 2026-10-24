using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Govor.Application.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Govor.Server.Tests;

public partial class ReadApiTests
{
    [Test]
    public async Task PasswordAttemptsAreRateLimitedWithoutThrottlingBackgroundRecoveryReads()
    {
        var created = PasswordProtocol.Create("http-rate-limit-password");
        var user = await _db.Context.Users.SingleAsync(u => u.Id == _db.Bob);
        user.SrpSalt = created.Credentials.Salt; user.SrpIdentity = created.Credentials.Identity;
        user.SrpVerifier = created.Credentials.Verifier; user.CredentialVersion = 1;
        await _db.Context.SaveChangesAsync();
        Authorize();
        for (var attempt = 0; attempt < 31; attempt++)
        {
            using var read = await _client.GetAsync("/api/auth/password");
            Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        _client.DefaultRequestHeaders.Authorization = null;
        for (var attempt = 0; attempt < 31; attempt++)
        {
            using var proof = await _client.PostAsJsonAsync("/api/auth/password/challenge",
                new PasswordChallengeRequest(user.Username, new string('0', 1024)));
            Assert.That(proof.StatusCode, Is.EqualTo(attempt == 30 ? HttpStatusCode.TooManyRequests : HttpStatusCode.BadRequest));
        }
    }

    [Test]
    public async Task SingleRegistrationRouteAcceptsSrpAndReturnsTokensForProtectedPasswordConfiguration()
    {
        Assert.That((await _client.GetAsync("/api/auth/password")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        var created = PasswordProtocol.Create("http-registration-password");
        var invitation = await _db.Context.Invitations.SingleAsync(i => i.Code == "test");
        invitation.Code = Guid.NewGuid().ToString("N");
        await _db.Context.SaveChangesAsync();
        object Request(string? password = null) => new { name = "НовыйУчастник456", inviteLink = invitation.Code,
            deviceInfo = "HTTP phone", srpSalt = created.Credentials.Salt, srpIdentity = created.Credentials.Identity,
            srpVerifier = created.Credentials.Verifier, password };
        Assert.That((await _client.PostAsJsonAsync("/api/auth/register", Request("plaintext-password"))).StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest));
        var registered = await _client.PostAsJsonAsync("/api/auth/register", Request());
        Assert.That(registered.StatusCode, Is.EqualTo(HttpStatusCode.OK), await registered.Content.ReadAsStringAsync());
        using var tokens = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.RootElement.GetProperty("accessToken").GetString());
        var configResponse = await _client.GetAsync("/api/auth/password");
        Assert.That(configResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), await configResponse.Content.ReadAsStringAsync());
        Assert.That(configResponse.Headers.CacheControl?.NoStore, Is.True);
        var config = (await configResponse.Content.ReadFromJsonAsync<PasswordConfiguration>())!;
        Assert.That(config.Credentials, Is.EqualTo(created.Credentials));
        Assert.That(config.Envelope, Is.Null);
        var user = await _db.Context.Users.AsNoTracking().SingleAsync(u => u.Username == "НовыйУчастник456");
        Assert.That(user.PasswordHash, Is.EqualTo("srp-only"));
        Assert.That(user.CredentialVersion, Is.EqualTo(1));
        Assert.That((await _client.PostAsJsonAsync("/api/auth/login", new { name = user.Username,
            password = "http-registration-password", deviceInfo = "legacy" })).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task HttpPasswordProofOpensSessionAndChangeRoutesConsumeSessionBoundAuthorization()
    {
        const string password = "http-password-proof";
        var created = PasswordProtocol.Create(password);
        var user = await _db.Context.Users.SingleAsync(u => u.Id == _db.Bob);
        user.PasswordHash = "srp-only"; user.SrpSalt = created.Credentials.Salt;
        user.SrpIdentity = created.Credentials.Identity; user.SrpVerifier = created.Credentials.Verifier;
        user.CredentialVersion = 1; await _db.Context.SaveChangesAsync();

        async Task<(PasswordProof Proof, SecureRemotePassword.SrpClient Client, string Public, SecureRemotePassword.SrpSession Session)> Prove(string purpose)
        {
            var client = PasswordProtocol.Client(); var ephemeral = client.GenerateEphemeral();
            var route = purpose == "login" ? "challenge" : $"{purpose}/challenge";
            var response = await _client.PostAsJsonAsync("/api/auth/password/" + route, new PasswordChallengeRequest(user.Username, ephemeral.Public, "test phone"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
            var challenge = (await response.Content.ReadFromJsonAsync<PasswordChallenge>())!;
            var derived = PasswordProtocol.Derive(password, challenge.Salt, challenge.Identity);
            var session = client.DeriveSession(ephemeral.Secret, challenge.PublicEphemeral, challenge.Salt, challenge.Identity,
                client.DerivePrivateKey(challenge.Salt, challenge.Identity, derived.Secret));
            return (new(challenge.ChallengeId, session.Proof), client, ephemeral.Public, session);
        }
        var login = await Prove("login");
        var response = await _client.PostAsJsonAsync("/api/auth/password/login", login.Proof);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var authenticated = (await response.Content.ReadFromJsonAsync<PasswordLoginResult>())!;
        login.Client.VerifySession(login.Public, login.Session, authenticated.ServerProof);
        Assert.That((await _client.PostAsJsonAsync("/api/auth/password/login", login.Proof)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authenticated.AccessToken);
        var action = await Prove("change");
        var grantResponse = await _client.PostAsJsonAsync("/api/auth/password/change/authorize", action.Proof);
        Assert.That(grantResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), await grantResponse.Content.ReadAsStringAsync());
        var grant = (await grantResponse.Content.ReadFromJsonAsync<PasswordActionResult>())!;
        action.Client.VerifySession(action.Public, action.Session, grant.ServerProof);
        var staleLogin = await Prove("login");
        var replacement = PasswordProtocol.Create("http-replacement-password");
        var request = new ChangePasswordRequest(grant.AuthorizationId, replacement.Credentials, null);
        Assert.That((await _client.PostAsJsonAsync("/api/auth/password/change", request)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync("/api/auth/password/change", request)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _client.PostAsJsonAsync("/api/auth/password/login", staleLogin.Proof)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _client.GetAsync("/api/auth/password")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
