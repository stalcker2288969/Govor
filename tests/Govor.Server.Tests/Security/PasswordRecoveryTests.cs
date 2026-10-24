using System.Security.Cryptography;
using System.Text.Json;
using Govor.Application.Authentication;
using Govor.Application.E2ee;
using Govor.Application.Infrastructure.AdminsStuff;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;
using SecureRemotePassword;

namespace Govor.Server.Tests;

[TestFixture]
public class PasswordRecoveryTests
{
    private const string Password = "correct-horse-2026";
    private TestDatabase _db = null!;
    private PasswordAccountService _service = null!;
    private PasswordChallenges _challenges = null!;
    private PasswordCredentials _credentials = null!;
    private string _archiveKey = null!, _root = null!, _code = null!, _name = null!;
    private UserSession _session = null!;
    [OneTimeSetUp]
    public void Keys()
    {
        (_credentials, _archiveKey) = PasswordProtocol.Create(Password);
        _root = E2eeRecoveryProtocol.GenerateIdentity(); _code = E2eeRecoveryProtocol.GenerateCode();
    }
    [SetUp]
    public async Task Setup()
    {
        _db = new(); _challenges = new(); _service = new(_db.Context, _challenges, new PasswordHasher());
        var user = await _db.Context.Users.SingleAsync(u => u.Id == _db.Alice);
        user.SrpSalt = _credentials.Salt; user.SrpIdentity = _credentials.Identity; user.SrpVerifier = _credentials.Verifier;
        user.PasswordHash = "srp-only"; user.CredentialVersion = 1; _name = user.Username;
        _session = new() { UserId = user.Id, ExpiresAt = DateTime.UtcNow.AddDays(2), RefreshTokenHash = Guid.NewGuid().ToString() };
        _db.Context.UserSessions.Add(_session);
        await _db.Context.SaveChangesAsync();
        var vault = E2eeRecoveryProtocol.Seal(user.Id, 1, _root, _code, "archived private keys");
        await new E2eeService(_db.Context).SaveRecoveryAsync(user.Id, new(0, vault.IdentityKey, vault.Ciphertext, vault.Signature));
        await _service.SaveEnvelopeAsync(user.Id, PasswordProtocol.Seal(user.Id, 1, _credentials, _root, _code, _archiveKey));
    }
    [TearDown] public void Cleanup() => _db.Dispose();

    private async Task<(PasswordProof Proof, SrpClient Client, string Public, SrpSession Session)> Proof(string action = "login", string password = Password)
    {
        var client = PasswordProtocol.Client(); var ephemeral = client.GenerateEphemeral();
        var challenge = await _service.StartAsync(new(_name, ephemeral.Public, "test device"), action,
            action == "login" ? null : _db.Alice, action == "login" ? null : _session.Id);
        var derived = PasswordProtocol.Derive(password, challenge.Salt, challenge.Identity);
        var session = client.DeriveSession(ephemeral.Secret, challenge.PublicEphemeral, challenge.Salt, challenge.Identity,
            client.DerivePrivateKey(challenge.Salt, challenge.Identity, derived.Secret));
        return (new(challenge.ChallengeId, session.Proof), client, ephemeral.Public, session);
    }
    private async Task<Guid> Grant(string action)
    {
        var proof = await Proof(action);
        var result = await _service.AuthorizeAsync(_db.Alice, _session.Id, proof.Proof, action);
        proof.Client.VerifySession(proof.Public, proof.Session, result.ServerProof);
        return result.AuthorizationId;
    }

    [Test]
    public async Task PasswordIsProvedWithoutBeingSentAndArchiveKeyIsIndependentOfSessionKey()
    {
        var proof = await Proof();
        var verified = await _service.VerifyAsync(proof.Proof);
        proof.Client.VerifySession(proof.Public, proof.Session, verified.ServerProof);
        Assert.That(verified.User.Id, Is.EqualTo(_db.Alice));
        Assert.That(JsonSerializer.Serialize(proof.Proof), Does.Not.Contain(Password).And.Not.Contain(_archiveKey));
        var config = await _service.GetAsync(_db.Alice);
        Assert.That(PasswordProtocol.Open(_db.Alice, config.Credentials, config.Envelope!, _archiveKey), Is.EqualTo(_code));
        Assert.Catch(() => PasswordProtocol.Open(_db.Alice, config.Credentials, config.Envelope!,
            Jose.Base64Url.Encode(SHA256.HashData(Convert.FromHexString(proof.Session.Key)))));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.VerifyAsync(proof.Proof));
    }

    [Test]
    public async Task IncorrectPasswordAndCrossPurposeProofAreRejected()
    {
        var wrong = await Proof(password: "incorrect-password");
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.VerifyAsync(wrong.Proof));
        var action = await Proof("reset");
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.VerifyAsync(action.Proof));
        var other = await Proof("change");
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.AuthorizeAsync(_db.Bob, _session.Id, other.Proof, "change"));
    }

    [Test]
    public async Task UnknownAccountReceivesAProofChallengeRatherThanALegacyPasswordRequest()
    {
        var challenge = await _service.StartAsync(new("not-an-account", PasswordProtocol.Client().GenerateEphemeral().Public));
        Assert.That(challenge.Legacy, Is.False);
        Assert.That(challenge.PublicEphemeral, Has.Length.EqualTo(1024));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.VerifyAsync(new(challenge.ChallengeId, new string('0', 128))));
        Assert.ThrowsAsync<ArgumentException>(() => _service.StartAsync(new(_name, new string('0', 1024))));
    }

    [Test]
    public async Task KnownPasswordChangeRewrapsSameArchiveAndRevokesOtherSessions()
    {
        var other = new UserSession { UserId = _db.Alice, ExpiresAt = DateTime.UtcNow.AddDays(1), RefreshTokenHash = "another-session" };
        _db.Context.UserSessions.Add(other); await _db.Context.SaveChangesAsync();
        var replacement = PasswordProtocol.Create("replacement-password-2026");
        var envelope = PasswordProtocol.Seal(_db.Alice, 2, replacement.Credentials, _root, _code, replacement.ArchiveKey);
        var grant = await Grant("change");
        await _service.ChangeAsync(_db.Alice, _session.Id, new(grant, replacement.Credentials, envelope));
        var config = await _service.GetAsync(_db.Alice);
        Assert.That(PasswordProtocol.Open(_db.Alice, config.Credentials, config.Envelope!, replacement.ArchiveKey), Is.EqualTo(_code));
        Assert.That((await new E2eeService(_db.Context).GetRecoveryAsync(_db.Alice)).Vault!.IdentityKey, Is.EqualTo(E2eeRecoveryProtocol.PublicIdentity(_root)));
        Assert.That((await _db.Context.UserSessions.AsNoTracking().SingleAsync(s => s.Id == other.Id)).IsRevoked, Is.True);
        Assert.That((await _db.Context.UserSessions.AsNoTracking().SingleAsync(s => s.Id == _session.Id)).IsRevoked, Is.False);
        var old = await Proof(); Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.VerifyAsync(old.Proof));
        var updated = await Proof(password: "replacement-password-2026"); await _service.VerifyAsync(updated.Proof);
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ChangeAsync(_db.Alice, _session.Id, new(grant, replacement.Credentials, envelope)));
    }

    [Test]
    public async Task InvalidPasswordChangeRollsBackCredentialsAndPreservesHistory()
    {
        var replacement = PasswordProtocol.Create("replacement-password-2026");
        var grant = await Grant("change");
        Assert.ThrowsAsync<ArgumentException>(() => _service.ChangeAsync(_db.Alice, _session.Id, new(grant, replacement.Credentials, null)));
        var config = await _service.GetAsync(_db.Alice);
        Assert.That(config.CredentialVersion, Is.EqualTo(1));
        Assert.That(PasswordProtocol.Open(_db.Alice, config.Credentials, config.Envelope!, _archiveKey), Is.EqualTo(_code));
    }

    [Test]
    public async Task ForgottenPasswordResetDoesNotReencryptHistoryWithTheAdministratorsNewPassword()
    {
        await new UsersService(_db.Context, new PasswordHasher()).SetPasswordAsync(_db.Alice, "administrator-reset-password");
        var user = await _db.Context.Users.AsNoTracking().SingleAsync(u => u.Id == _db.Alice);
        var vault = await _db.Context.E2eeRecoveryVaults.AsNoTracking().SingleAsync();
        Assert.That(user.SrpVerifier, Is.Null);
        Assert.That(vault.PasswordCiphertext, Is.Null);
        Assert.That(E2eeRecoveryProtocol.Open((await new E2eeService(_db.Context).GetRecoveryAsync(_db.Alice)).Vault!, _db.Alice, _code),
            Is.EqualTo("archived private keys"));
        Assert.That((await _db.Context.UserSessions.AsNoTracking().SingleAsync(s => s.Id == _session.Id)).IsRevoked, Is.True);
    }

    [Test]
    public async Task ExplicitEncryptionResetPublishesIdentityChangeAndNewPasswordRecoveryEnvelope()
    {
        var e2ee = new E2eeService(_db.Context);
        var formerKeys = E2eeProtocol.GenerateKeys();
        var formerDevice = E2eeRecoveryProtocol.Certify(E2eeProtocol.PublicKey(formerKeys, _db.Alice, _session.Id), _root);
        await e2ee.RegisterAsync(_db.Alice, _session.Id, formerDevice);
        _db.AddMessage(sender: _db.Alice);
        var previous = (await new E2eeService(_db.Context).GetRecoveryAsync(_db.Alice)).Vault!;
        var newRoot = E2eeRecoveryProtocol.GenerateIdentity(); var newCode = E2eeRecoveryProtocol.GenerateCode();
        var vault = E2eeRecoveryProtocol.Seal(_db.Alice, 2, newRoot, newCode, "new history only");
        var envelope = PasswordProtocol.Seal(_db.Alice, 1, _credentials, newRoot, newCode, _archiveKey);
        var grant = await Grant("reset");
        var reset = new ResetEncryptionRequest(grant, previous.IdentityKey, previous.Revision, vault, envelope);
        var change = await _service.ResetAsync(_db.Alice, _session.Id, reset);
        _db.Context.ChangeTracker.Clear(); // Device enrollment is a subsequent HTTP request with a fresh context.
        var newKeys = E2eeProtocol.GenerateKeys();
        await e2ee.RegisterAsync(_db.Alice, _session.Id,
            E2eeRecoveryProtocol.Certify(E2eeProtocol.PublicKey(newKeys, _db.Alice, _session.Id), newRoot));
        var historical = await e2ee.GetHistoricalSignerAsync(_db.Bob, _db.ChatId, Govor.Domain.Models.Messages.RecipientType.User,
            _session.Id, formerDevice.IdentityKey);
        Assert.That(historical, Is.EqualTo(formerDevice));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => e2ee.GetHistoricalSignerAsync(_db.Outsider, _db.ChatId,
            Govor.Domain.Models.Messages.RecipientType.User, _session.Id, formerDevice.IdentityKey));
        Assert.That(change.PreviousIdentityKey, Is.EqualTo(previous.IdentityKey));
        Assert.That(change.Epoch, Is.EqualTo(1));
        Assert.That((await new E2eeService(_db.Context).GetIdentityChangesAsync(_db.Bob, _db.Alice)), Has.Length.EqualTo(1));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => new E2eeService(_db.Context).GetIdentityChangesAsync(_db.Outsider, _db.Alice));
        var group = new Govor.Domain.Models.ChatGroup { Id = Guid.NewGuid(), Name = "Past author", Description = "" };
        var membership = new Govor.Domain.Models.GroupMembership { GroupId = group.Id, UserId = _db.Outsider, MemberSince = DateTime.UtcNow };
        _db.Context.ChatGroups.Add(group); _db.Context.GroupMemberships.Add(membership);
        await _db.Context.SaveChangesAsync();
        _db.AddMessage(sender: _db.Alice, chat: group.Id, type: Govor.Domain.Models.Messages.RecipientType.Group);
        Assert.That(await new E2eeService(_db.Context).GetIdentityChangesAsync(_db.Outsider, _db.Alice), Has.Length.EqualTo(1));
        membership.IsBanned = true; await _db.Context.SaveChangesAsync();
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => new E2eeService(_db.Context).GetIdentityChangesAsync(_db.Outsider, _db.Alice));
        var config = await _service.GetAsync(_db.Alice);
        Assert.That(PasswordProtocol.Open(_db.Alice, config.Credentials, config.Envelope!, _archiveKey), Is.EqualTo(newCode));
        var currentVault = (await new E2eeService(_db.Context).GetRecoveryAsync(_db.Alice)).Vault!;
        Assert.Catch(() => E2eeRecoveryProtocol.Open(currentVault, _db.Alice, _code));
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ResetAsync(_db.Alice, _session.Id, reset));
    }

    [Test]
    public async Task StaleEncryptionResetDoesNotReplaceTheExistingIdentity()
    {
        var root = E2eeRecoveryProtocol.GenerateIdentity(); var code = E2eeRecoveryProtocol.GenerateCode();
        var vault = E2eeRecoveryProtocol.Seal(_db.Alice, 3, root, code, "new history");
        var envelope = PasswordProtocol.Seal(_db.Alice, 1, _credentials, root, code, _archiveKey);
        var grant = await Grant("reset");
        Assert.ThrowsAsync<InvalidOperationException>(() => _service.ResetAsync(_db.Alice, _session.Id,
            new(grant, E2eeRecoveryProtocol.PublicIdentity(_root), 2, vault, envelope)));
        Assert.That((await new E2eeService(_db.Context).GetRecoveryAsync(_db.Alice)).Vault!.IdentityKey, Is.EqualTo(E2eeRecoveryProtocol.PublicIdentity(_root)));
    }

    [Test]
    public async Task LegacyEnrollmentChecksOldPasswordAndRequiresADifferentReplacement()
    {
        var account = await _db.Context.Users.SingleAsync(u => u.Id == _db.Alice);
        account.SrpSalt = null; account.SrpIdentity = null; account.SrpVerifier = null;
        account.PasswordHash = new PasswordHasher().Hash(Password);
        await _db.Context.SaveChangesAsync();
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.EnrollAsync(_db.Alice, new(_credentials, "wrong-password")));
        Assert.ThrowsAsync<ArgumentException>(() => _service.EnrollAsync(_db.Alice, new(_credentials, Password)));
        var replacement = PasswordProtocol.Create("upgraded-password-2026");
        await _service.EnrollAsync(_db.Alice, new(replacement.Credentials, Password));
        Assert.That((await _service.GetAsync(_db.Alice)).Credentials, Is.EqualTo(replacement.Credentials));
        Assert.ThrowsAsync<InvalidOperationException>(() => _service.EnrollAsync(_db.Alice, new(replacement.Credentials, Password)));
    }

    [Test]
    public async Task ExpiredProofAndExpiredActionGrantCannotBeUsed()
    {
        var clock = new TestClock(); _challenges = new(clock); _service = new(_db.Context, _challenges, new PasswordHasher());
        var proof = await Proof(); clock.Now = clock.Now.AddMinutes(3);
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.VerifyAsync(proof.Proof));
        var grant = await Grant("change"); clock.Now = clock.Now.AddMinutes(3);
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ChangeAsync(_db.Alice, _session.Id, new(grant, _credentials, null)));
    }

    private sealed class TestClock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
