using Govor.Domain.Models.Users;

namespace Govor.Application.Friends;

public interface IFriendshipService
{
    Task RemoveFriendAsync(Guid userId, Guid currentUserId);
    Task<List<User>> GetFriendsAsync(Guid userId);
    Task<List<User>> GetPotentialFriendsAsync(Guid userId);
    Task<List<User>> SearchUsersAsync(string query, Guid currentId);
}
