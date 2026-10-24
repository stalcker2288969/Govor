using Govor.Application.Authentication;
using Govor.Domain;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;

namespace Govor.Application.Infrastructure.AdminsStuff;

public class UsersService : IUsersAdministration
{
    private readonly GovorDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    
    public UsersService(GovorDbContext context, IPasswordHasher passwordHasher)
    {
        _context = context;
        _passwordHasher = passwordHasher;
    }

    public async Task<List<User>> GetAllUsersAsync()
    {
        var results = await _context.Users
            .AsNoTracking()
            .Take(50)
            .ToListAsync();
        return results;
    }

    public async Task SetPasswordAsync(Guid userId, string password)
    {
        var user = await GetUserById(userId);

        if (user is null)
            return;

        user.PasswordHash = _passwordHasher.Hash(password);
        user.SrpVerifier = null;
        user.SrpSalt = null;
        user.SrpIdentity = null;
        user.CredentialVersion++;
        var recovery = await _context.E2eeRecoveryVaults.SingleOrDefaultAsync(v => v.UserId == userId);
        if (recovery is not null)
        {
            // An administrator can reset authentication, but cannot rewrap an
            // encryption key without knowing a client-side recovery secret.
            recovery.PasswordCiphertext = null;
            recovery.PasswordSignature = null;
            recovery.PasswordCredentialVersion = 0;
        }

        var sessions = await _context.UserSessions.Where(s => s.UserId == userId && !s.IsRevoked).ToListAsync();
        foreach (var session in sessions)
            session.IsRevoked = true;
        var pushTokens = await _context.UserPushTokens.Where(t => t.UserId == userId).ToListAsync();
        foreach (var token in pushTokens)
            token.IsActive = false;

        await _context.SaveChangesAsync();
    }
    
    public async Task<User> GetUserById(Guid userId)
    {
        var result = await _context.Users.FirstOrDefaultAsync(user => user.Id == userId);
            
        return result;
    }
}
