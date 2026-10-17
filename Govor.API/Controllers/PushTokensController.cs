using Govor.API.Common.Extensions;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.PushNotifications;
using Govor.Contracts.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Govor.API.Controllers;

[ApiController]
[Authorize(Roles = "Admin, User")]
[Route("api")]
public class PushTokensController : Controller
{
    private readonly ICurrentUserService _currentUser;
    private readonly ICurrentUserSessionService _currentSession;
    private readonly IPushTokenService _pushTokenService;
    private readonly ILogger<PushTokensController> _logger;

    public PushTokensController(
        IPushTokenService  pushTokenService,
        ICurrentUserService currentUser,
        ICurrentUserSessionService currentSession,
        ILogger<PushTokensController> logger)
    {
        _pushTokenService = pushTokenService;
        _currentUser = currentUser;
        _currentSession = currentSession;
        _logger = logger;
    }

    [HttpPost("pushes/token/register")]
    public async Task<IActionResult> RegisterToken([FromBody] RegisterPushTokenRequest req)
    {
        try
        {
            var result = await _pushTokenService.AddOrUpdateTokenAsync(
                _currentUser.GetCurrentUserId(), _currentSession.GetUserSessionId(), req.Token, req.Platform);
            return result.ToActionResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register push token for the current session");
            return Problem(statusCode: StatusCodes.Status500InternalServerError,
                detail: "Push token registration failed. Please try again later.");
        }
    }

    [HttpDelete("pushes/token")]
    public async Task<IActionResult> UnregisterToken()
    {
        var result = await _pushTokenService.DeactivateTokenBySessionAsync(_currentSession.GetUserSessionId());
        return result.ToActionResult();
    }
}
