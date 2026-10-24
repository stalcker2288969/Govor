using System.Security.Cryptography;
using Govor.Application.E2ee;
using Govor.Application.Infrastructure.Extensions;
using Govor.Domain.Models.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Govor.API.Controllers;

[ApiController]
[Authorize(Roles = "User,Admin")]
[Route("api/e2ee")]
public sealed class E2eeController(E2eeService e2ee, ICurrentUserService user,
    ICurrentUserSessionService session) : ControllerBase
{
    [HttpPut("device")]
    public async Task<IActionResult> Register(DeviceKey key)
    {
        try
        {
            await e2ee.RegisterAsync(user.GetCurrentUserId(), session.GetUserSessionId(), key);
            return Ok(new { registered = true });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException) { return Conflict("Device keys already exist for this session."); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        { return BadRequest("Invalid E2EE public keys."); }
    }

    [HttpGet("conversations/{type:int}/{conversationId:guid}/keys")]
    public async Task<IActionResult> Keys(int type, Guid conversationId)
    {
        if (type is < 0 or > 1 || conversationId == Guid.Empty) return BadRequest();
        try
        {
            return Ok(await e2ee.GetConversationKeysAsync(user.GetCurrentUserId(), conversationId, (RecipientType)type));
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or ArgumentException or FormatException)
        { return Conflict("Every participant must have valid E2EE keys. Update clients and register their devices."); }
    }
}
