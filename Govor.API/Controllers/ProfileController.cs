using AutoMapper;
using Govor.API.Common.Extensions;
using Govor.API.Hubs;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.Medias;
using Govor.Application.Profiles;
using Govor.Contracts.DTOs;
using Govor.Contracts.Requests;
using Govor.Domain.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SmartRes;

namespace Govor.API.Controllers;

[ApiController]
[Route("api/profile")]
[Authorize(Roles = "Admin,User")]
public class ProfileController : ControllerBase
{
    private readonly ILogger<ProfileController> _logger;
    private readonly IMediaService _mediaService;
    private readonly IProfileService _profileService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IHubContext<ProfileHub> _profileHubContext;
    private readonly IMapper _mapper;

    public ProfileController(
        ILogger<ProfileController> logger,
        IMediaService mediaService,
        IProfileService profileService,
        ICurrentUserService currentUserService,
        IHubContext<ProfileHub> profileHubContext,
        IMapper mapper)
    {
        _logger = logger;
        _mediaService = mediaService;
        _profileService = profileService;
        _profileHubContext = profileHubContext;
        _currentUserService = currentUserService;
        _mapper = mapper;
    }
    
    [HttpPost("avatar")] // api/profile/avatar
    public async Task<IActionResult> UploadAvatar([FromForm] AvatarUploadRequest request)
    {
        var userId = _currentUserService.GetCurrentUserId();
        
        if (request?.FromFile == null || request.FromFile.Length == 0)
        {
            return BadRequest("File is empty.");
        }
        
        try
        {
            byte[] fileBytes = await ReadFileAsync(request.FromFile);

            var media = new Media(
                _currentUserService.GetCurrentUserId(),
                DateTime.UtcNow,
                Path.GetFileName(request.FromFile.FileName),
                fileBytes,
                request.Type,
                request.MimeType,
                String.Empty,
                MediaOwnerType.Avatar,
                userId);

            var mediaInfo = await _mediaService.UploadMediaAsync(media)
                .TapAsync(m => _logger.LogInformation("Uploaded avatar file {filename} by user {id}.",
                    request.FromFile.FileName, userId))
                .TapAsync(mediaInfo => _profileService.SetNewIcon(userId, mediaInfo.MediaId));
                
            return mediaInfo.ToActionResult();
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, new { Message = "Avatar processing error.", Details = ex.Message });
        }
    }
    
    private async Task<byte[]> ReadFileAsync(IFormFile file)
    {
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        return ms.ToArray();
    }
    
    [HttpGet("download/me")]
    public async Task<IActionResult> DownloadProfile()
    {
        try
        {
            var userId = _currentUserService.GetCurrentUserId();
            var result = await _profileService.GetUserProfileAsync(userId);
            
            return result
                .Map(user => _mapper.Map<UserProfileDto>(user))
                .ToActionResult();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex.Message);
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
            return StatusCode(500, "Unexpected Error! Please try again later.");
        }
    }

    [HttpGet("download/{id}")]
    public async Task<IActionResult> DownloadProfileByUserId(Guid id)
    {
        try
        {
            var user = (await _profileService.GetUserProfileAsync(id))
                .Map(user => _mapper.Map<UserProfileDto>(user));
            
          return user.ToActionResult();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex.Message);
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, ex.Message);
            return StatusCode(500, "Unexpected Error! Please try again later.");
        }
    }
}
