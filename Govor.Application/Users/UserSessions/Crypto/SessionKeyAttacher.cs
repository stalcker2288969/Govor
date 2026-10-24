using Govor.Application.Infrastructure.Extensions;
using Govor.Domain.Models.Users.Crypto;
using Govor.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Govor.Application.Users.UserSessions.Crypto;

public class SessionKeyAttacher : ISessionKeyAttacher
{
    private readonly ILogger<SessionKeyAttacher> _logger;
    private readonly ICurrentUserService _current;
    private readonly GovorDbContext _context;

    public SessionKeyAttacher(ILogger<SessionKeyAttacher> logger, ICurrentUserService current, GovorDbContext context)
    {
        _logger = logger;
        _current = current;
        _context = context;
    }
    
    public async Task AttachKeysAsync(
        Guid sessionId,
        byte[] identityKey,
        byte[] signedPreKey,
        byte[] signedPreKeySignature,
        IEnumerable<byte[]> oneTimePreKeys)
    {
        var session = await _context.UserSessions
            .Include(s => s.CryptoSession)
            .ThenInclude(c => c.OneTimePreKeys)
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session == null)
            throw new ArgumentException("Session not found", nameof(sessionId));

        if (session.CryptoSession != null)
            throw new InvalidOperationException("Keys are already attached to this session");
        
        if(session.UserId != _current.GetCurrentUserId())
            throw new InvalidOperationException("You cannot attach keys to this session");
        
        var cryptoSessionId = Guid.NewGuid();
        
        var cryptoSession = new UserCryptoSession
        {
            Id = cryptoSessionId,
            UserSessionId = sessionId,
            PublicIdentityKey = identityKey,
            SignedPreKey = new SignedPreKey()
            {
                Id = Guid.NewGuid(),
                UserCryptoSessionId = cryptoSessionId,
                PublicSignedPreKey = signedPreKey,
                SignedPreKeySignature = signedPreKeySignature,
            },
            OneTimePreKeys = oneTimePreKeys.Select(key => new OneTimePreKey
            {
                Id = Guid.NewGuid(),
                PublicKey = key,
                IsUsed = false,
                UploadedAt = DateTime.UtcNow
            }).ToList()
        };

        session.CryptoSession = cryptoSession;
        _context.UserCryptoSessions.Add(cryptoSession);

        await _context.SaveChangesAsync();

        _logger.LogInformation("Attached keys to session {SessionId}", sessionId);
    }
}
