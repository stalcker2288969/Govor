using System.Security.Cryptography;
using Govor.Application.E2ee;
using Govor.Domain;
using Govor.Domain.Models.Users;
using Govor.Domain.Models.Users.Crypto;
using Microsoft.EntityFrameworkCore;

namespace Govor.Application.Authentication;

public sealed class PasswordAccountService(GovorDbContext db, PasswordChallenges challenges, IPasswordHasher legacyHasher)
{
    public static PasswordCredentials Credentials(User user) => user.SrpVerifier is null
        ? throw new InvalidOperationException("This account needs a one-time password upgrade.")
        : new(1, user.SrpSalt!, user.SrpIdentity!, user.SrpVerifier);

    public async Task<PasswordChallenge> StartAsync(PasswordChallengeRequest request, string purpose = "login",
        Guid? user = null, Guid? session = null)
    {
        if (!PasswordProtocol.GroupElement(request.PublicEphemeral) || request.Name is null || request.Name.Length > 50 ||
            request.DeviceInfo is null || request.DeviceInfo.Length > 256) throw new ArgumentException("Invalid password challenge.");
        var account = user.HasValue ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == user) :
            await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username == request.Name);
        if (account is { SrpVerifier: null })
            return new(Guid.Empty, "", "", "", account.CredentialVersion, true);
        // Unknown names receive a normal-looking challenge and always fail proof verification.
        var credentials = account is null ? FakeCredentials() : Credentials(account);
        var ephemeral = PasswordProtocol.Server().GenerateEphemeral(credentials.Verifier);
        var entry = new PasswordChallenges.Entry(account?.Id ?? Guid.Empty, session ?? Guid.Empty, purpose,
            account?.CredentialVersion ?? 0, credentials, ephemeral.Secret, request.PublicEphemeral,
            request.DeviceInfo, challenges.Now.AddMinutes(2));
        var id = challenges.Add(entry);
        return new(id, credentials.Salt, credentials.Identity, ephemeral.Public, entry.CredentialVersion);
    }

    public async Task<(User User, string ServerProof, string DeviceInfo)> VerifyAsync(PasswordProof proof,
        string purpose = "login", Guid? user = null, Guid? session = null)
    {
        var entry = challenges.Consume(proof.ChallengeId, purpose, user, session);
        if (!PasswordProtocol.Hex(proof.Proof, 128)) throw new UnauthorizedAccessException("Invalid password proof.");
        string serverProof;
        try
        {
            var result = PasswordProtocol.Server().DeriveSession(entry.SecretEphemeral, entry.PublicEphemeral,
                entry.Credentials.Salt, entry.Credentials.Identity, entry.Credentials.Verifier, proof.Proof);
            serverProof = result.Proof;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { throw new UnauthorizedAccessException("Incorrect password or invalid proof."); }
        var account = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == entry.UserId);
        if (account is null || account.CredentialVersion != entry.CredentialVersion ||
            account.SrpVerifier != entry.Credentials.Verifier) throw new UnauthorizedAccessException("Credentials changed. Sign in again.");
        return (account, serverProof, entry.DeviceInfo);
    }

    public async Task<PasswordActionResult> AuthorizeAsync(Guid user, Guid session, PasswordProof proof, string action)
    {
        var verified = await VerifyAsync(proof, action, user, session);
        var grant = new PasswordChallenges.Entry(user, session, action + "-authorized", verified.User.CredentialVersion,
            Credentials(verified.User), "", "", "", challenges.Now.AddMinutes(2));
        return new(challenges.Add(grant), verified.ServerProof, verified.User.CredentialVersion);
    }

    public async Task<PasswordLoginResult> LoginAsync(PasswordProof proof, Govor.Application.Users.UserSessions.IUserSessionOpener sessions)
    {
        var verified = await VerifyAsync(proof);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            if (await db.Users.Where(u => u.Id == verified.User.Id && u.CredentialVersion == verified.User.CredentialVersion)
                    .ExecuteUpdateAsync(u => u.SetProperty(u => u.CredentialVersion, u => u.CredentialVersion)) != 1)
                throw new UnauthorizedAccessException("Credentials changed during login.");
            var result = await sessions.OpenSessionAsync(verified.User, verified.DeviceInfo);
            if (result.IsFailure) throw new InvalidOperationException("Unable to open the authenticated session.");
            await tx.CommitAsync();
            return new PasswordLoginResult(result.Value.refreshToken, result.Value.accessToken, verified.ServerProof, verified.User.CredentialVersion);
        });
    }

    public async Task<PasswordConfiguration> GetAsync(Guid user)
    {
        var account = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user);
        var credentials = Credentials(account);
        var recovery = await db.E2eeRecoveryVaults.AsNoTracking().SingleOrDefaultAsync(v => v.UserId == user);
        var envelope = recovery is { PasswordCiphertext: not null, PasswordSignature: not null } &&
            recovery.PasswordCredentialVersion == account.CredentialVersion
            ? new PasswordEnvelope(recovery.IdentityKey, recovery.PasswordCredentialVersion, recovery.PasswordCiphertext, recovery.PasswordSignature) : null;
        return new(credentials, account.CredentialVersion, envelope);
    }

    public async Task EnrollAsync(Guid user, PasswordEnrollmentRequest request)
    {
        PasswordProtocol.Validate(request.Credentials);
        var account = await db.Users.SingleAsync(u => u.Id == user);
        if (account.SrpVerifier is not null) throw new InvalidOperationException("Password authentication is already upgraded.");
        if (request.LegacyPassword is null || request.LegacyPassword.Length > 256 || !legacyHasher.Verify(request.LegacyPassword, account.PasswordHash))
            throw new UnauthorizedAccessException("Incorrect current password.");
        if (request.LegacyPassword.Length >= 8)
        {
            var old = PasswordProtocol.Derive(request.LegacyPassword, request.Credentials.Salt, request.Credentials.Identity);
            var client = PasswordProtocol.Client();
            if (client.DeriveVerifier(client.DerivePrivateKey(request.Credentials.Salt, request.Credentials.Identity, old.Secret)) == request.Credentials.Verifier)
                throw new ArgumentException("The replacement password must differ from the former plaintext password.");
        }
        Apply(account, request.Credentials);
        await db.SaveChangesAsync();
    }

    public async Task SaveEnvelopeAsync(Guid user, PasswordEnvelope envelope)
    {
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            if (await db.Users.Where(u => u.Id == user && u.CredentialVersion == envelope.CredentialVersion)
                    .ExecuteUpdateAsync(u => u.SetProperty(u => u.CredentialVersion, u => u.CredentialVersion)) != 1)
                throw new InvalidOperationException("Password changed. Retry.");
            var account = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user);
            PasswordProtocol.ValidateEnvelope(user, Credentials(account), envelope);
            var recovery = await db.E2eeRecoveryVaults.SingleAsync(v => v.UserId == user);
            if (recovery.IdentityKey != envelope.IdentityKey) throw new InvalidOperationException("Encryption identity changed. Retry.");
            recovery.PasswordCredentialVersion = envelope.CredentialVersion;
            recovery.PasswordCiphertext = envelope.Ciphertext;
            recovery.PasswordSignature = envelope.Signature;
            await db.SaveChangesAsync(); await tx.CommitAsync();
        });
    }

    public async Task ChangeAsync(Guid user, Guid session, ChangePasswordRequest request)
    {
        PasswordProtocol.Validate(request.Credentials);
        var grant = challenges.Consume(request.AuthorizationId, "change-authorized", user, session);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            if (await db.Users.Where(u => u.Id == user && u.CredentialVersion == grant.CredentialVersion)
                    .ExecuteUpdateAsync(u => u.SetProperty(u => u.CredentialVersion, u => u.CredentialVersion)) != 1)
                throw new InvalidOperationException("Credentials changed. Retry.");
            var account = await db.Users.SingleAsync(u => u.Id == user);
            if (account.CredentialVersion != grant.CredentialVersion) throw new InvalidOperationException("Credentials changed. Retry.");
            var recovery = await db.E2eeRecoveryVaults.SingleOrDefaultAsync(v => v.UserId == user);
            if (recovery is not null)
            {
                if (request.Envelope is null || request.Envelope.IdentityKey != recovery.IdentityKey ||
                    request.Envelope.CredentialVersion != account.CredentialVersion + 1)
                    throw new ArgumentException("Changing a known password must preserve the encrypted recovery key.");
                PasswordProtocol.ValidateEnvelope(user, request.Credentials, request.Envelope);
                recovery.PasswordCiphertext = request.Envelope.Ciphertext;
                recovery.PasswordSignature = request.Envelope.Signature;
                recovery.PasswordCredentialVersion = request.Envelope.CredentialVersion;
            }
            Apply(account, request.Credentials);
            await RevokeOtherSessionsAsync(user, session);
            await db.SaveChangesAsync(); await tx.CommitAsync();
        });
    }

    public async Task<AccountIdentityChange> ResetAsync(Guid user, Guid session, ResetEncryptionRequest request)
    {
        var grant = challenges.Consume(request.AuthorizationId, "reset-authorized", user, session);
        E2eeRecoveryProtocol.Validate(request.Vault);
        PasswordProtocol.ValidateEnvelope(user, grant.Credentials, request.Envelope);
        if (request.Vault.UserId != user || request.Vault.Revision != request.ExpectedRevision + 1 ||
            request.Envelope.IdentityKey != request.Vault.IdentityKey || request.Envelope.CredentialVersion != grant.CredentialVersion)
            throw new ArgumentException("Invalid encryption reset context.");
        AccountIdentityChange? change = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            if (await db.Users.Where(u => u.Id == user && u.CredentialVersion == grant.CredentialVersion)
                    .ExecuteUpdateAsync(u => u.SetProperty(u => u.CredentialVersion, u => u.CredentialVersion)) != 1)
                throw new InvalidOperationException("Credentials changed. Retry.");
            var account = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user);
            if (account.CredentialVersion != grant.CredentialVersion) throw new InvalidOperationException("Credentials changed. Retry.");
            var recovery = await db.E2eeRecoveryVaults.SingleAsync(v => v.UserId == user);
            if (recovery.IdentityKey != request.ExpectedIdentityKey || recovery.Revision != request.ExpectedRevision ||
                recovery.IdentityKey == request.Vault.IdentityKey) throw new InvalidOperationException("Encryption identity changed. Retry.");
            change = new(user, recovery.Epoch + 1, recovery.IdentityKey, request.Vault.IdentityKey,
                challenges.Now.ToUnixTimeMilliseconds());
            var previousDevice = (await new E2eeService(db).GetOwnDevicesAsync(user)).SingleOrDefault(d => d.SessionId == session);
            db.E2eeIdentityChanges.Add(new() { UserId = user, Epoch = change.Epoch, PreviousIdentityKey = change.PreviousIdentityKey,
                IdentityKey = change.IdentityKey, ChangedAt = change.ChangedAt,
                PreviousSessionId = previousDevice?.SessionId,
                PreviousDeviceKeyJson = previousDevice is null ? null : System.Text.Json.JsonSerializer.Serialize(previousDevice) });
            recovery.Epoch = change.Epoch;
            recovery.Revision = request.Vault.Revision;
            recovery.IdentityKey = request.Vault.IdentityKey;
            recovery.Ciphertext = request.Vault.Ciphertext;
            recovery.Signature = request.Vault.Signature;
            recovery.PasswordCiphertext = request.Envelope.Ciphertext;
            recovery.PasswordSignature = request.Envelope.Signature;
            recovery.PasswordCredentialVersion = request.Envelope.CredentialVersion;
            await RevokeOtherSessionsAsync(user, session);
            // Keep historical public certificates, but remove the current
            // session's former identity so it can register under the new root.
            await db.UserCryptoSessions.Where(c => c.UserSessionId == session).ExecuteDeleteAsync();
            await db.SaveChangesAsync(); await tx.CommitAsync();
        });
        return change!;
    }

    private async Task RevokeOtherSessionsAsync(Guid user, Guid currentSession)
    {
        await db.UserSessions.Where(s => s.UserId == user && s.Id != currentSession)
            .ExecuteUpdateAsync(s => s.SetProperty(s => s.IsRevoked, true));
        await db.UserPushTokens.Where(t => t.UserId == user && t.UserSessionId != currentSession)
            .ExecuteUpdateAsync(t => t.SetProperty(t => t.IsActive, false));
    }
    private static void Apply(User user, PasswordCredentials credentials)
    {
        user.PasswordHash = "srp-only";
        user.SrpSalt = credentials.Salt; user.SrpIdentity = credentials.Identity; user.SrpVerifier = credentials.Verifier;
        user.CredentialVersion++;
    }
    private static PasswordCredentials FakeCredentials()
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var identity = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return new(1, salt, identity, PasswordProtocol.Client().DeriveVerifier(Convert.ToHexString(RandomNumberGenerator.GetBytes(64))));
    }
}
