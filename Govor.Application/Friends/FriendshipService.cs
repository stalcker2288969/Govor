using Microsoft.EntityFrameworkCore;
using Govor.Domain;
using Govor.Domain.Models;
using Govor.Domain.Models.Users;

namespace Govor.Application.Friends;

public class FriendshipService : IFriendshipService
{
    private readonly GovorDbContext _context;
    
    public FriendshipService(GovorDbContext context)
    {
        _context = context;
    }

    public async Task RemoveFriendAsync(Guid userId, Guid currentUserId)
    {
        if (userId == Guid.Empty || currentUserId == Guid.Empty || userId == currentUserId)
            throw new ArgumentException("Two distinct users are required.");
        // A removal must never clear a block or cancel a pending request.
        await _context.Friendships.Where(f => f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == userId && f.AddresseeId == currentUserId) ||
             (f.RequesterId == currentUserId && f.AddresseeId == userId)))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, FriendshipStatus.Rejected));
    }
    
    public async Task<List<User>> SearchUsersAsync(string query, Guid currentId)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }
        
        return await _context.Users
            .AsNoTracking()
            .Where(u => u.Id != currentId && u.Username.Contains(query))
            .Take(5)
            .ToListAsync();
    }
    
    public async Task<List<User>> GetPotentialFriendsAsync(Guid userId)
    {
        var pendingFriendships = await _context.Friendships
            .AsNoTracking()
            .Include(f => f.Requester)
            .Include(f => f.Addressee)
            .Where(f => (f.RequesterId == userId || f.AddresseeId == userId) 
                        && f.Status == FriendshipStatus.Pending)
            .ToListAsync();
        
        return pendingFriendships
            .Select(f => f.RequesterId == userId ? f.Addressee : f.Requester)
            .ToList();
    }

    public async Task<List<User>> GetFriendsAsync(Guid userId)
    {
        var acceptedFriendships = await _context.Friendships
            .AsNoTracking()
            .Include(f => f.Requester)
            .Include(f => f.Addressee)
            .Where(f => (f.RequesterId == userId || f.AddresseeId == userId) 
                        && f.Status == FriendshipStatus.Accepted)
            .ToListAsync();
        
        var friends = acceptedFriendships
            .Select(f => f.RequesterId == userId ? f.Addressee : f.Requester)
            .ToList();
        
        return friends;
    }
}
