using Govor.Domain;
using Govor.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Govor.Application.Friends;

public class FriendsBlockService(GovorDbContext context) : IFriendsBlockService
{
    public async Task BlockFriendRequestAsync(Guid userId, Guid currentUserId)
    {
        if (userId == Guid.Empty || currentUserId == Guid.Empty || userId == currentUserId)
            throw new ArgumentException("Two distinct users are required.");
        if (await context.Users.CountAsync(u => u.Id == userId || u.Id == currentUserId) != 2)
            throw new KeyNotFoundException("User not found.");
        var friendship = await FindAsync(userId, currentUserId);
        if (friendship?.Status == FriendshipStatus.Blocked)
        {
            if (friendship.RequesterId != currentUserId)
                throw new UnauthorizedAccessException("Only the user who blocked this conversation can change the block.");
            return;
        }
        if (friendship is null)
        {
            friendship = new Friendship { Id = Guid.NewGuid() };
            context.Friendships.Add(friendship);
        }
        // A blocked row records its owner as Requester; requests cannot overwrite it.
        friendship.RequesterId = currentUserId;
        friendship.AddresseeId = userId;
        friendship.Status = FriendshipStatus.Blocked;
        await context.SaveChangesAsync();
    }

    public async Task UnblockFriendRequestAsync(Guid userId, Guid currentUserId)
    {
        if (userId == Guid.Empty || currentUserId == Guid.Empty || userId == currentUserId)
            throw new ArgumentException("Two distinct users are required.");
        var friendship = await FindAsync(userId, currentUserId);
        if (friendship?.Status != FriendshipStatus.Blocked) return;
        if (friendship.RequesterId != currentUserId)
            throw new UnauthorizedAccessException("Only the user who blocked this conversation can remove the block.");
        // Unblocking permits a new request; it does not restore friendship without consent.
        friendship.Status = FriendshipStatus.Rejected;
        await context.SaveChangesAsync();
    }

    private Task<Friendship?> FindAsync(Guid userId, Guid currentUserId) => context.Friendships.FirstOrDefaultAsync(f =>
        (f.RequesterId == userId && f.AddresseeId == currentUserId) ||
        (f.RequesterId == currentUserId && f.AddresseeId == userId));
}
