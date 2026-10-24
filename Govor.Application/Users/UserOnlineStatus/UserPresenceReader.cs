using Govor.Domain;
using Microsoft.EntityFrameworkCore;

namespace Govor.Application.Users.UserOnlineStatus;

public class UserPresenceReader(GovorDbContext context) : IUserPresenceReader
{
    public Task<DateTime?> GetLastSeenAsync(Guid userId)
    {
        return context.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => (DateTime?)u.WasOnline).FirstOrDefaultAsync();
    }
}
