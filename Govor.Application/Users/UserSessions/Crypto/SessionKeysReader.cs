using Govor.Domain.Models.Users.Crypto;
using Govor.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Govor.Application.Users.UserSessions.Crypto;

public class SessionKeysReader : ISessionKeysReader
{
    private readonly ILogger<SessionKeyAttacher> _logger;
    private readonly GovorDbContext _context;

    public SessionKeysReader(ILogger<SessionKeyAttacher> logger, GovorDbContext context)
    {
        _logger = logger;
        _context = context;
    }
    
    public async Task<bool> HasKeysAttachedAsync(Guid sessionId)
    {
        return await _context.UserCryptoSessions.AnyAsync(c => c.UserSessionId == sessionId);
    }

    public async Task<IReadOnlyList<UserCryptoSession>> GetAllActiveKeysAsync(Guid userId)
    {
        _logger.LogInformation("Getting all active keys for user {UserId}.", userId);
        
        var now = DateTime.UtcNow;

        return await _context.UserCryptoSessions
            .AsNoTracking()
            .Include(c => c.OneTimePreKeys)
            .Include(c => c.SignedPreKey)
            .Where(c => c.UserSession.UserId == userId
                        && !c.UserSession.IsRevoked
                        && c.UserSession.ExpiresAt > now)
            .ToListAsync();
    }

    public async Task<int> GetRemainingOneTimePreKeysCountAsync(Guid sessionId)
    {
        _logger.LogInformation("Getting count of one time pre keys for session {UserId}.", sessionId);

        return await _context.OneTimePreKeys
            .AsNoTracking()
            .Include(f => f.UserCryptoSession)
            .CountAsync(f => f.UserCryptoSession.UserSessionId == sessionId && !f.IsUsed);
    }

    public async Task<UserCryptoSession?> GetKeysBySessionIdAsync(Guid sessionId)
    {
        _logger.LogInformation("Getting keys for session {session}.", sessionId);
        
        return await _context.UserCryptoSessions
            .AsNoTracking()
            .Include(c => c.OneTimePreKeys)
            .Include(c => c.UserSession)
            .Include(c => c.SignedPreKey)
            .FirstOrDefaultAsync(c => c.UserSessionId == sessionId);
    }
}
