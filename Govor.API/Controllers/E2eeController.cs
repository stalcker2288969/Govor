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
public sealed class E2eeController(
    E2eeService e2ee,
    ICurrentUserService user,
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
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Conflict("Device generation changed. Reload the recovery archive and retry.");
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            return BadRequest("Invalid E2EE public keys.");
        }
    }

    [HttpGet("recovery")]
    public async Task<IActionResult> Recovery() => Ok(await e2ee.GetRecoveryAsync(user.GetCurrentUserId()));

    [HttpGet("devices")]
    public async Task<IActionResult> Devices() => Ok(await e2ee.GetOwnDevicesAsync(user.GetCurrentUserId()));

    [HttpPut("devices/certificate")]
    public async Task<IActionResult> CertifyHistory(DeviceKey key)
    {
        try { await e2ee.CertifyHistoricalDeviceAsync(user.GetCurrentUserId(), key); return Ok(new { certified = true }); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException)
        { return Conflict(); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        { return BadRequest(); }
    }

    [HttpGet("conversations/{type:int}/{conversationId:guid}/signers/{deviceSession:guid}")]
    public async Task<IActionResult> HistoricalSigner(int type, Guid conversationId, Guid deviceSession)
    {
        if (type is < 0 or > 1) return BadRequest();
        try
        {
            var key = await e2ee.GetHistoricalSignerAsync(user.GetCurrentUserId(), conversationId, (RecipientType)type, deviceSession);
            return key is null ? NotFound() : Ok(key);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    [HttpPut("recovery")]
    [RequestSizeLimit(4_100_000)]
    public async Task<IActionResult> SaveRecovery(SaveRecoveryVault request)
    {
        try { return Ok(await e2ee.SaveRecoveryAsync(user.GetCurrentUserId(), request)); }
        catch (InvalidOperationException) { return Conflict("Recovery archive revision changed; merge and retry."); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException or
                                       System.Text.Json.JsonException or KeyNotFoundException)
        { return BadRequest("Invalid signed recovery archive."); }
    }

    [HttpGet("conversations/{type:int}/{conversationId:guid}/keys")]
    public async Task<IActionResult> Keys(int type, Guid conversationId)
    {
        if (type is < 0 or > 1 || conversationId == Guid.Empty) return BadRequest();
        try
        {
            return Ok(await e2ee.GetConversationKeysAsync(user.GetCurrentUserId(), conversationId,
                (RecipientType)type));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or ArgumentException
                                       or FormatException)
        {
            return Conflict("Every participant must have valid E2EE keys. Update clients and register their devices.");
        }
    }
}
