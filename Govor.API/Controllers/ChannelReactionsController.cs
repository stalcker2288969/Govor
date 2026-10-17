using AutoMapper;
using Govor.API.Common.Extensions;
using Govor.API.Hubs.Infrastructure;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.Reactions;
using Govor.Contracts.Requests;
using Govor.Contracts.Responses.SignalR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Govor.API.Controllers;

[ApiController]
[Authorize(Roles = "Admin,User")]
[Route("api/groups/{groupId:guid}/reactions")]
public class ChannelReactionsController(
    IMessageReactionService reactions,
    ICurrentUserService currentUser,
    IMapper mapper,
    IChatNotificationService notifier) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid groupId) => 
        (await reactions.GetPolicyAsync(currentUser.GetCurrentUserId(), groupId))
        .Map(p => mapper.Map<ChannelReactionPolicyResponse>(p))
        .ToActionResult();

    [HttpPut]
    public async Task<IActionResult> Set(Guid groupId, [FromBody] ChannelReactionPolicyRequest request)
    {
        var result = await reactions.SetPolicyAsync(
            currentUser.GetCurrentUserId(),
            groupId,
            request.Mode,
            request.ReactionIds);
       
        if (result.IsFailure) 
            return result.ToActionResult();
        
        var response = mapper.Map<ChannelReactionPolicyResponse>(result.Value);
        
        await notifier.NotifyChannelReactionPolicyChangedAsync(response);
        
        return Ok(response);
    }
}
