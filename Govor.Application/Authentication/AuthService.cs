using Govor.Application.Authentication.Exceptions;
using Govor.Application.Infrastructure.Validators;
using Govor.Application.Infrastructure.Common;
using Govor.Application.Users;
using Govor.Domain;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Users;
using Microsoft.EntityFrameworkCore;
using SmartRes;

namespace Govor.Application.Authentication;

public class AuthService : IAccountService
{
    private readonly GovorDbContext _context; 
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUserNameExistValidator _userNameExistValidator;
    private readonly IUsernameValidator _usernameValidator;
    private readonly INowDateTimeProvider _clock;
    
    public AuthService(
        GovorDbContext context,
        IUserNameExistValidator existValidator,
        IPasswordHasher passwordHasher,
        IUsernameValidator usernameValidator,
        INowDateTimeProvider clock)
    {
        _context = context;
        _userNameExistValidator = existValidator;
        _passwordHasher = passwordHasher;
        _usernameValidator = usernameValidator;
        _clock = clock;
    }
    
    public Task<Result<User, Error>> RegistrationAsync(string name, string password, Invitation invitation) =>
        RegisterCoreAsync(name, password, invitation, null);

    public Task<Result<User, Error>> RegistrationWithPasswordProofAsync(string name, PasswordCredentials credentials, Invitation invitation)
    {
        PasswordProtocol.Validate(credentials);
        return RegisterCoreAsync(name, null, invitation, credentials);
    }

    private async Task<Result<User, Error>> RegisterCoreAsync(string name, string? password, Invitation invitation, PasswordCredentials? credentials)
    {
        var validationResult = _usernameValidator.Validate(name);
        if (validationResult.IsFailure)
        {
            return Result.Failure<User>(validationResult.Error);
        }
        
        if (await _userNameExistValidator.IsUsernameExistsAsync(name))
        {
            return Result.Failure<User>(Error.Conflict(
                 nameof(UserAlreadyExistException), 
                $"User with username '{name}' already exists."));
        }
        
        var passwordHash = credentials is null ? _passwordHasher.Hash(password!) : "srp-only";
        
        var operationId = Guid.NewGuid();
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var completed = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == operationId);
            if (completed is not null) return Result.Success(completed);
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Serialize configuration changes with registration: account and membership commit together.
                await _context.ServerCommunitySettings.Where(s => s.Id == 1)
                    .ExecuteUpdateAsync(s => s.SetProperty(s => s.AllowLeave, s => s.AllowLeave));
                
                var settings = await _context.ServerCommunitySettings.AsNoTracking().SingleAsync(s => s.Id == 1);
                
                if (settings.RequiredChannelId.HasValue &&
                    !await _context.ChatGroups.AnyAsync(g => g.Id == settings.RequiredChannelId && g.IsChannel))
                    return Result.Failure<User>(Error.Validation("Auth.RequiredChannelUnavailable", "The required channel is unavailable."));
                
                var now = _clock.Now;
                
                var reserved = await _context.Invitations.Where(i => i.Id == invitation.Id &&
                    i.IsActive && i.EndDate > now && i.Participants < i.MaxParticipants &&
                    _context.Users.Count(u => u.InviteId == i.Id) < i.MaxParticipants)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.Participants, i => i.Participants + 1));
                
                if (reserved == 0)
                    return Result.Failure<User>(Error.Validation("Auth.InvitationUnavailable", "Registration invitation is expired or full."));
                
                var user = new User
                {
                    Id = operationId, Username = name, PasswordHash = passwordHash, Description = string.Empty,
                    CreatedOn = DateOnly.FromDateTime(now), IconId = Guid.Empty, WasOnline = now, InviteId = invitation.Id,
                    SrpSalt = credentials?.Salt, SrpIdentity = credentials?.Identity, SrpVerifier = credentials?.Verifier,
                    CredentialVersion = credentials is null ? 0 : 1
                };
                
                await _context.Users.AddAsync(user);
                await SetRoleAsync(user, invitation);
                
                if (settings.RequiredChannelId.HasValue)
                {
                    _context.GroupMemberships.Add(new GroupMembership
                    {
                        Id = Guid.NewGuid(), GroupId = settings.RequiredChannelId.Value, UserId = user.Id, MemberSince = now
                    });
                }
                
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                
                return Result.Success(user);
            }
            catch
            {
                _context.ChangeTracker.Clear();
                throw;
            }
        });
    }
    public async Task<Result<User, Error>> LoginAsync(string name, string password)
    {
        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == name);
        
        if (user is null)
        {
            return Result.Failure<User>(Error.NotFound(
                nameof(UserNotRegisteredException), 
                $"User '{name}' is not registered."));
        }
        
        if (user.SrpVerifier is not null)
            return Result.Failure<User>(Error.Unauthorized("Auth.PasswordProofRequired", "Use password proof authentication with an updated client."));
        if (!_passwordHasher.Verify(password, user.PasswordHash))
        {
            return Result.Failure<User>(Error.Failure(
                nameof(InvalidOperationException), 
                "The password provided is incorrect."));
        }
        
        return user; // Success 
    }
    
    private async Task SetRoleAsync(User user, Invitation invitation)
    {
        if (invitation.IsAdmin)
        {
            await _context.Admins.AddAsync(new Admin { UserId = user.Id });
        }
    }
}
