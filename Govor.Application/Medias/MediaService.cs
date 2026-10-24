using Govor.Application.Storage;
using Govor.Domain.Models;
using Govor.Domain;
using Govor.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartRes;

namespace Govor.Application.Medias;

public class MediaService : IMediaService
{
    private ILogger<MediaService> _logger;
    private IStorageService _storageService;
    private GovorDbContext _dbContext;
    
    public MediaService(IStorageService storageService, GovorDbContext dbContext, ILogger<MediaService> logger)
    {
        _storageService = storageService;
        _dbContext = dbContext;
        _logger = logger;
    }
    
    public async Task<Result<MediaUploadResult, Error>> UploadMediaAsync(Media file)
    {
        try
        {
            var url = await _storageService.SaveAsync(file.Data, file.FileName);

            var mediaId = Guid.NewGuid();
            
            _dbContext.MediaFiles.Add(new MediaFile()
            {
                Id = mediaId, 
                UploaderId = file.UploaderId,
                DateCreated = file.UploadedOn,
                MediaType = file.Type,
                MineType = file.MimeType,
                Url = url,
                OwnerType = file.OwnerType,
                OwnerId = file.OwnerId,
            });
            
            await _dbContext.SaveChangesAsync();
            
            _logger.LogInformation($"Media uploaded: {url} with id: {mediaId} by {file.UploaderId}");
            
            return new MediaUploadResult(mediaId, url);
        }
        catch (ArgumentException ex)
        {
            return Result<MediaUploadResult, Error>.Failure(Error.Failure(
                nameof(InvalidOperationException), 
                $"An error occured while uploading the media file: {ex.Message}")
            ); 
        }
    }

    public async Task<Result<Unit, Error>> DeleteMediaAsync(Guid mediaId)
    {
        var mediaFile = await _dbContext.MediaFiles
            .FirstOrDefaultAsync(x => x.Id == mediaId);
        
        if (mediaFile is null)
        {
            return Result<Unit, Error>.Failure(Error.NotFound(
                "File.DeleteMedia", 
                $"File with given id ({mediaId}) doesn't exist!")
            );
        }
        
        await _storageService.RemoveAsync(mediaFile.Url);
        
        _dbContext.MediaFiles.Remove(mediaFile);

        await _dbContext.SaveChangesAsync();
        
        return new Unit();
    }

    public async Task<Result<Media, Error>> GetMediaByUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Result.Failure<Media>(Error.Validation("File.GetMediaByUrl", "Media URL is required."));
        var file = await _dbContext.MediaFiles.AsNoTracking().FirstOrDefaultAsync(m => m.Url == url);
        return await ReadMediaAsync(file, "File.GetMediaByUrl");
    }

    public async Task<Result<Media, Error>> GetMediaByIdAsync(Guid mediaId)
    {
        var file = await _dbContext.MediaFiles.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaId);
        return await ReadMediaAsync(file, "File.GetMediaById");
    }

    private async Task<Result<Media, Error>> ReadMediaAsync(MediaFile? mediaFile, string errorCode)
    {
        try
        {
            if (mediaFile is null)
            {
                return Result<Media, Error>.Failure(Error.NotFound(
                    errorCode,
                    "Media file does not exist.")
                );
            }
            
            // Загрузить бинарные данные из хранилища
            await using Stream dataStream = await _storageService.LoadAsync(mediaFile.Url);

            // Считать поток в byte[]
            using var memoryStream = new MemoryStream();
            await dataStream.CopyToAsync(memoryStream);
            var contentBytes = memoryStream.ToArray();
            
            _logger.LogInformation("Media found: {mediaFile.MediaType} with id: {mediaFile.Id} and url: {mediaFile.Url}", 
                mediaFile.MediaType, mediaFile.Id, mediaFile.Url);
            
            // Вернуть объект Media
            return new Media(
                mediaFile.UploaderId,
                mediaFile.DateCreated,
                Path.GetFileName(mediaFile.Url),
                contentBytes,
                mediaFile.MediaType,
                mediaFile.MineType,
                string.Empty,
                mediaFile.OwnerType,
                mediaFile.OwnerId
            );
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Media file {MediaId} not found on storage.", mediaFile?.Id);
            return Result<Media, Error>.Failure(Error.ServerError(errorCode, "Media file not found on storage!"));
        }
    }

    public async Task<bool> HasMediaAsync(Guid mediaId)
    {
        return await _dbContext.MediaFiles.AsNoTracking()
            .AnyAsync(x => x.Id == mediaId);
    }

    public async Task<bool> HasMediaByUrlAsync(string url)
    {
        return await _dbContext.MediaFiles.AsNoTracking()
            .AnyAsync(x => x.Url == url);
    }

    public async Task<Result<Unit, Error>> AttachToMessageAsync(Guid mediaId, Guid messageId)
    {
        var mediaFile = await _dbContext.MediaFiles
            .FirstOrDefaultAsync(x => x.Id == mediaId);

        if (mediaFile is null)
        {
            return Result<Unit, Error>.Failure(Error.NotFound(
                "File.AttachToMessage", 
                $"File with given id ({mediaId}) doesn't exist!")
            );
        }
        
        if (mediaFile.OwnerType != MediaOwnerType.Message)
        {
            _logger.LogWarning("Attempt to attach already owned media {MediaId}", mediaId);
            return Result<Unit, Error>.Failure(Error.Failure(
                "File.AttachToMessage", 
                $"Media {mediaId} is already attached to {mediaFile.OwnerType}")
            );
        }

        mediaFile.OwnerType = MediaOwnerType.Message;
        mediaFile.OwnerId = messageId;

        _dbContext.MediaFiles.Update(mediaFile);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Media {MediaId} successfully attached to message {MessageId}", mediaId, messageId);
        
        return new Unit();
    }
}
