using Govor.Application.Infrastructure.Extensions;
using Govor.Domain.Models.Users.Crypto;
using Govor.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Govor.Application.Users.UserSessions.Crypto;

public class OneTimePreKeysRotator : IOneTimePreKeysRotator
{
    private readonly ILogger<SessionKeyAttacher> _logger;
    private readonly ICurrentUserService _current;
    private readonly GovorDbContext _context;

    public OneTimePreKeysRotator(ILogger<SessionKeyAttacher> logger, ICurrentUserService current, GovorDbContext context)
    {
        _logger = logger;
        _current = current;
        _context = context;
    }
    
    public async Task RotateOneTimePreKeysAsync(Guid sessionId, IEnumerable<byte[]> newOneTimePreKeys)
    {
        var cryptoSession = await _context.UserCryptoSessions
            .Include(c => c.OneTimePreKeys)
            .Include(c => c.UserSession)
            .FirstOrDefaultAsync(c => c.UserSessionId == sessionId);

        if (cryptoSession == null)
            throw new ArgumentException("Crypto session not found", nameof(sessionId));
        if (cryptoSession.UserSession.UserId != _current.GetCurrentUserId())
            throw new UnauthorizedAccessException("Only the session owner can rotate its keys.");

        // Удаляем все использованные ключи
        var usedKeys = cryptoSession.OneTimePreKeys.Where(k => k.IsUsed).ToList();
        if (usedKeys.Any())
        {
            _context.OneTimePreKeys.RemoveRange(usedKeys);
        }

        // Добавляем новые ключи (возможно, стоит ограничить количество)
        foreach (var key in newOneTimePreKeys)
        {
            _context.OneTimePreKeys.Add(new OneTimePreKey
            {
                Id = Guid.NewGuid(),
                UserCryptoSessionId = cryptoSession.Id,
                PublicKey = key,
                IsUsed = false,
                UploadedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();
    }

    public async Task MarkOneTimePreKeyAsUsedAsync(Guid sessionId, Guid oneTimePreKeyId)
    {
        var key = await _context.OneTimePreKeys
            .Include(k => k.UserCryptoSession)
            .ThenInclude(c => c.UserSession)
            .FirstOrDefaultAsync(k => k.Id == oneTimePreKeyId && k.UserCryptoSession.UserSessionId == sessionId);

        if (key == null)
            throw new ArgumentException("One-Time PreKey not found for this session", nameof(oneTimePreKeyId));
        if (key.UserCryptoSession.UserSession.UserId != _current.GetCurrentUserId())
            throw new UnauthorizedAccessException("Only the session owner can consume its keys.");

        if (key.IsUsed)
            return; // Уже помечен

        key.IsUsed = true;

        await _context.SaveChangesAsync();
    }
}
