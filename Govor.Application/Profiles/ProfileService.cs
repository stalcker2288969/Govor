using Govor.Domain;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartRes;

namespace Govor.Application.Profiles;

public class ProfileService : IProfileService
{
    private readonly ILogger<ProfileService> _logger;
    private readonly GovorDbContext _context;

    public ProfileService(GovorDbContext context, ILogger<ProfileService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Result<UserProfile, Error>> GetUserProfileAsync(Guid userId)
    {
        _logger.LogInformation("Getting user {UserId} profile", userId);
     
        var profile = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(user => new UserProfile
            {
                Id = user.Id,
                Username = user.Username,
                Description = user.Description,
                IconId = user.IconId
            })
            .FirstOrDefaultAsync();

        if (profile is null)
        {
            return Result.Failure<UserProfile>(CreateNotFoundError(userId));
        }
        
        profile.Benefits = await new UserBenefitsService(_context).GetAsync(userId);
        return profile;
    }

    public async Task<Result<Unit, Error>> SetDescription(string description, Guid userId)
    {
        _logger.LogInformation("Updating description for user {UserId}", userId);
        
        var updatedRows = await _context.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.Description, description));

        if (updatedRows == 0)
        {
            return Result.Failure(CreateNotFoundError(userId));
        }

        return Result.Success();
    }

    public async Task<Result<Unit, Error>> SetNewIcon(Guid userId, Guid iconId)
    {
        _logger.LogInformation("Updating icon for user {UserId}", userId);

        if (!await _context.MediaFiles.AnyAsync(f => f.Id == iconId && f.UploaderId == userId &&
                f.OwnerType == MediaOwnerType.Avatar))
            return Result<Unit, Error>.Failure(Error.Forbidden("Profile.IconAccess", "Only your uploaded avatar can be used."));

       
        var updatedRows = await _context.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.IconId, iconId));

        if (updatedRows == 0)
        {
            return Result.Failure(CreateNotFoundError(userId));
        }
        
        return Result.Success();
    }
    
    private static Error CreateNotFoundError(Guid userId) => 
        Error.NotFound("Profile.UserNotFound", $"User with ID {userId} was not found.");
}
