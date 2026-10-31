using Govor.Application.Infrastructure.Extensions;
using Govor.Application.Profiles;
using Govor.Contracts.Requests;
using Govor.Domain.Models.Users;
using Govor.API.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Govor.API.Controllers;

[ApiController]
[Authorize]
public sealed class UserBenefitsController(UserBenefitsService benefits, ICurrentUserService currentUser,
    IHubContext<ProfileHub> profiles, ILogger<UserBenefitsController> logger) : ControllerBase
{
    [HttpGet("api/profile/{userId:guid}/benefits")]
    public Task<IActionResult> Get(Guid userId) => Execute(() => benefits.GetAsync(userId));

    [Authorize(Roles = "Admin")]
    [HttpPut("api/admin/users/{userId:guid}/privileges")]
    public Task<IActionResult> SetPrivileges(Guid userId, SetPrivilegesRequest request) =>
        Execute(() => Change(userId, () => benefits.SetPrivilegesAsync(userId, request.Privileges)));

    [HttpPut("api/profile/tag")]
    public Task<IActionResult> SetTag(ProfileTagStyle tag) =>
        Execute(() => Change(currentUser.GetCurrentUserId(), () => benefits.SetTagAsync(currentUser.GetCurrentUserId(), tag)));

    [HttpDelete("api/profile/tag")]
    public Task<IActionResult> ResetTag() => Execute(() => Change(currentUser.GetCurrentUserId(),
        () => benefits.SetTagAsync(currentUser.GetCurrentUserId(), null)));

    private async Task<UserBenefitsView> Change(Guid userId, Func<Task<UserBenefitsView>> save)
    {
        var value = await save();
        try
        {
            await profiles.Clients.All.SendAsync("UserBenefitsUpdated", new { userId, benefits = value });
        }
        catch (Exception ex)
        {
            // The committed result stays successful. Reconnecting clients reload the profile snapshot.
            logger.LogError(ex, "Failed to broadcast benefits revision {Revision} for {UserId}", value.Revision, userId);
        }
        return value;
    }

    private async Task<IActionResult> Execute(Func<Task<UserBenefitsView>> action)
    {
        try { return Ok(await action()); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "Benefits changed concurrently. Reload and retry." }); }
    }
}

