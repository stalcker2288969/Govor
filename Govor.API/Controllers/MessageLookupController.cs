using AutoMapper;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.Messages;
using Govor.Contracts.Responses;
using Govor.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Govor.API.Controllers;

/// <summary>Loads a reply target outside the current history page without exposing another conversation.</summary>
[ApiController]
[Authorize]
[Route("api/messages")]
public sealed class MessageLookupController(GovorDbContext db, ICurrentUserService currentUser, IMapper mapper) : ControllerBase
{
    [HttpGet("{messageId:guid}")]
    public async Task<IActionResult> Get(Guid messageId)
    {
        var message = await db.Messages.AsNoTracking().Include(m => m.MediaAttachments).ThenInclude(m => m.MediaFile)
            .Include(m => m.Reactions).Include(m => m.MessageViews).AsSplitQuery()
            .SingleOrDefaultAsync(m => m.Id == messageId);
        if (message is null || !await db.HasChatAccessAsync(currentUser.GetCurrentUserId(), message.RecipientId, message.RecipientType))
            return NotFound();
        return Ok(mapper.Map<MessageResponse>(message));
    }
}
