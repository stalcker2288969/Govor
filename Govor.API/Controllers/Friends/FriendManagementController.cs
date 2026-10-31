using Govor.Application.Friends;
using Govor.Application.Infrastructure.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Govor.API.Controllers.Friends;

[ApiController]
[Authorize]
[Route("api/friends/{userId:guid}")]
public sealed class FriendManagementController(IFriendshipService friends, IFriendsBlockService blocks,
    ICurrentUserService currentUser) : ControllerBase
{
    [HttpDelete]
    public Task<IActionResult> Remove(Guid userId) => Execute(() => friends.RemoveFriendAsync(userId, currentUser.GetCurrentUserId()));

    [HttpPut("block")]
    public Task<IActionResult> Block(Guid userId) => Execute(() => blocks.BlockFriendRequestAsync(userId, currentUser.GetCurrentUserId()));

    [HttpDelete("block")]
    public Task<IActionResult> Unblock(Guid userId) => Execute(() => blocks.UnblockFriendRequestAsync(userId, currentUser.GetCurrentUserId()));

    private async Task<IActionResult> Execute(Func<Task> action)
    {
        try { await action(); return NoContent(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
}
