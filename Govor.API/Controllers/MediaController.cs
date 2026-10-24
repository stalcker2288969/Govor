using Govor.API.Common.Extensions;
using Govor.Application.E2ee;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.Medias;
using Govor.Contracts.Requests;
using Govor.Domain.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Govor.API.Controllers;

[ApiController]
[Route("api/media")]
[Authorize(Roles = "User,Admin")]
public class MediaController : Controller
{
    private readonly ILogger<MediaController> _logger;
    private readonly IMediaService _mediaService;
    private readonly IAccesserToDownloadMedia _accesser;
    private readonly ICurrentUserService _currentUserService;

    public MediaController(
        ILogger<MediaController> logger,
        IMediaService mediaService,
        IAccesserToDownloadMedia accesser,
        ICurrentUserService currentUserService)
    {
        _logger = logger;
        _accesser = accesser;
        _mediaService = mediaService;
        _currentUserService = currentUserService;
    }

    [HttpPost("upload")]
    [RequestSizeLimit(20_000_000)] // ~20MB
    public async Task<IActionResult> Upload([FromForm] MediaUploadRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (request.OwnerType != MediaOwnerType.Message && request.OwnerType != MediaOwnerType.Avatar)
            return BadRequest("Only message and personal avatar uploads are supported.");

        if (request?.FromFile is null || request.FromFile.Length == 0)
            return BadRequest("No file uploaded");

        if (request.FromFile.Length > 20_000_000)
            return BadRequest("File is too large");

        if (string.IsNullOrWhiteSpace(request.MimeType))
            return BadRequest("Missing MIME type");

        if (request.OwnerType == MediaOwnerType.Message &&
            (request.MimeType != E2eeProtocol.EncryptedMimeType || !string.IsNullOrEmpty(request.EncryptedKey)))
            return BadRequest("Message attachments must be encrypted on the client. Do not upload decryption keys.");

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
                request.EncryptedKey ?? string.Empty,
                request.OwnerType,
                null)
            {
                OwnerType = request.OwnerType,
                OwnerId = request.OwnerType == MediaOwnerType.Avatar
            ? _currentUserService.GetCurrentUserId()
            : null,
            };

            var result = await _mediaService.UploadMediaAsync(media);

            _logger.LogInformation("Uploaded file {FileName} from user {UserId}",
                media.FileName, media.UploaderId);
            
            if (result.IsFailure)
            {
                _logger.LogWarning("Uploaded file {FileName} from user {UserId} finished with error: {Error}",
                    media.FileName, 
                    media.UploaderId, 
                    result.Error);
            }
            
            return result.ToActionResult();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, ex.Message);
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading media");
            return StatusCode(500, "Internal server error");
        }
    }

    private async Task<byte[]> ReadFileAsync(IFormFile file)
    {
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        return ms.ToArray();
    }


    [HttpGet("download/{id}")]
    public async Task<IActionResult> Download(Guid id)
    {
        var userId = _currentUserService.GetCurrentUserId();

        if (!await _accesser.HasAccessAsync(id, userId))
            return Forbid();

        var mediaResult = await _mediaService.GetMediaByIdAsync(id);
        
        if (mediaResult.IsFailure)
            return mediaResult.ToActionResult();
        
        var media = mediaResult.Value;
        return File(media.Data, media.MimeType, Path.GetFileName(media.FileName));
    }
}
