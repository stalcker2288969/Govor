using Govor.API.Common.Extensions;
using Govor.API.Hubs.Infrastructure;
using Govor.Application.Authentication;
using Govor.Application.Users.UserSessions;
using Govor.Contracts.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartRes;
using Govor.Application.Infrastructure.Extensions;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Cryptography;

namespace Govor.API.Controllers.Authentication;

[ApiController]
[Authorize(Roles = "User,Admin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/[controller]")]
public class AuthController : Controller
{
    private IUserSessionOpener _userSession;
    private IInvitesService _invitesService;
    private IAccountService _accountService;
    private ILogger<AuthController> _logger;
    private readonly IChatNotificationService _notifier;
    private readonly PasswordAccountService _passwords;
    private readonly ICurrentUserService _current;
    private readonly ICurrentUserSessionService _currentSession;
    
    public AuthController(
        IAccountService accountService,
        IInvitesService invitesService,
        IUserSessionOpener userSessionOpener,
        ILogger<AuthController> logger,
        IChatNotificationService notifier,
        PasswordAccountService passwords = null,
        ICurrentUserService current = null,
        ICurrentUserSessionService currentSession = null)
    {
        _userSession = userSessionOpener;
        _accountService = accountService;
        _invitesService = invitesService;
        _logger = logger;
        _notifier = notifier;
        _passwords = passwords;
        _current = current;
        _currentSession = currentSession;
    }

    [AllowAnonymous, EnableRateLimiting("password-auth"), HttpPost("register")] // api/auth/register
    public async Task<IActionResult> Register([FromBody] RegistrationRequest request)
    {
        _logger.LogInformation("Processing registration request for: {Name}", request.Name);
        
        PasswordCredentials credentials = null;
        if (request.SrpVerifier is not null)
        {
            credentials = new(1, request.SrpSalt, request.SrpIdentity, request.SrpVerifier);
            try { PasswordProtocol.Validate(credentials); }
            catch (CryptographicException) { return BadRequest("Invalid SRP credentials."); }
        }
        var result = await _invitesService.ValidateAsync(request.InviteLink)
            .BindAsync(invite => credentials is null
                ? _accountService.RegistrationAsync(request.Name, request.Password, invite)
                : _accountService.RegistrationWithPasswordProofAsync(request.Name, credentials, invite))
            .TapAsync(user => _logger.LogInformation("User {Username} ({Id}) registered successfully", user.Username, user.Id))
            .BindAsync(async user =>
            {
                // Registration has committed the required-channel membership at this point.
                await _notifier.NotifyUserJoinedGroupsAsync(user.Id);
                return await _userSession.OpenSessionAsync(user, request.DeviceInfo);
            });
        
        if (result.IsFailure)
        {
            _logger.LogWarning("Registration pipeline failed. Error: {Code} - {Message}", 
                result.Error.Code, result.Error.Message);
        }
        else
        {
            _logger.LogInformation("Session opened successfully for the request.");
        }
        
        return result.ToActionResult();
    }
    
    [AllowAnonymous, EnableRateLimiting("password-auth"), HttpPost("login")] // api/auth/login
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        _logger.LogInformation("Processing registration request for: {Name}", request.Name);
        
        var result = await _accountService.LoginAsync(request.Name, request.Password)
            .TapAsync(user => _logger.LogInformation("User {Username} ({Id}) logged in.", user.Username, user.Id))
            .BindAsync(user => _userSession.OpenSessionAsync(user, request.DeviceInfo));
    
        if (result.IsFailure)
        {
            _logger.LogWarning("Login pipeline failed. Error: {Code} - {Message}", 
                result.Error.Code, result.Error.Message);
        }
        else
        {
            _logger.LogInformation("Session opened successfully for the request.");
        }
        
        return result.ToActionResult();
    }
    [AllowAnonymous, EnableRateLimiting("password-auth"), HttpPost("password/challenge")]
    public Task<IActionResult> Challenge(PasswordChallengeRequest request) =>
        Execute(async () => Ok(await _passwords.StartAsync(request)));

    [AllowAnonymous, EnableRateLimiting("password-auth"), HttpPost("password/login")]
    public Task<IActionResult> Login(PasswordProof proof) => Execute(async () =>
    {
        return Ok(await _passwords.LoginAsync(proof, _userSession));
    });

    [HttpGet("password")]
    public Task<IActionResult> Configuration() =>
        Execute(async () => Ok(await _passwords.GetAsync(_current.GetCurrentUserId())));

    [HttpPut("password/enrollment")]
    public Task<IActionResult> Enroll(PasswordEnrollmentRequest request) => Execute(async () =>
    {
        await _passwords.EnrollAsync(_current.GetCurrentUserId(), request);
        return Ok(new { enrolled = true });
    });

    [HttpPut("password/recovery")]
    public Task<IActionResult> SaveEnvelope(SavePasswordEnvelope request) => Execute(async () =>
    {
        await _passwords.SaveEnvelopeAsync(_current.GetCurrentUserId(), request.Envelope);
        return Ok(new { saved = true });
    });

    [EnableRateLimiting("password-auth"), HttpPost("password/{purpose:regex(^(change|reset)$)}/challenge")]
    public Task<IActionResult> ActionChallenge(string purpose, PasswordChallengeRequest request) => Execute(async () =>
        Ok(await _passwords.StartAsync(request, purpose, _current.GetCurrentUserId(), _currentSession.GetUserSessionId())));

    [EnableRateLimiting("password-auth"), HttpPost("password/{purpose:regex(^(change|reset)$)}/authorize")]
    public Task<IActionResult> AuthorizeAction(string purpose, PasswordProof proof) => Execute(async () =>
        Ok(await _passwords.AuthorizeAsync(_current.GetCurrentUserId(), _currentSession.GetUserSessionId(), proof,
            purpose)));

    [HttpPost("password/change")]
    public Task<IActionResult> Change(ChangePasswordRequest request) => Execute(async () =>
    {
        await _passwords.ChangeAsync(_current.GetCurrentUserId(), _currentSession.GetUserSessionId(), request);
        return Ok(new { changed = true });
    });

    [HttpPost("password/reset-encryption")]
    [RequestSizeLimit(4_120_000)]
    public Task<IActionResult> Reset(ResetEncryptionRequest request) => Execute(async () =>
        Ok(await _passwords.ResetAsync(_current.GetCurrentUserId(), _currentSession.GetUserSessionId(), request)));

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized("Password proof is incorrect or expired.");
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Conflict("Account changed. Retry the operation.");
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException or
                                       System.Text.Json.JsonException or KeyNotFoundException)
        {
            return BadRequest("Invalid password authentication data.");
        }
    }
}
