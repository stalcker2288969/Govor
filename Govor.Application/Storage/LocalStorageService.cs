namespace Govor.Application.Storage;

public class LocalStorageService : IStorageService
{
    private readonly string _storagePath;
    
    public LocalStorageService(string contentRootPath)
    {
        _storagePath = Path.GetFullPath(Path.Combine(contentRootPath, "uploads"));
        
        if (!Directory.Exists(_storagePath))
        {
            Directory.CreateDirectory(_storagePath);
        }
    }
    
    public async Task<string> SaveAsync(byte[] data, string fileName)
    {
        if(data is null || data.Length == 0) 
            throw new ArgumentException("Invalid file", nameof(data));
        
        if(fileName is null || fileName.Length == 0)
            throw new ArgumentException("Invalid file name", nameof(fileName));
        
        var date = DateTime.UtcNow.ToString("yyyy/MM");
        
        var folder = Path.Combine(_storagePath, date);
        Directory.CreateDirectory(folder);
        
        var uniqueFileName = $"{Guid.NewGuid()}_{Path.GetFileName(fileName)}";
        var fullPath = Path.Combine(folder, uniqueFileName);
        
        await using var stream = new FileStream(fullPath, FileMode.Create);
        await stream.WriteAsync(data, 0, data.Length);
        
        return Path.Combine(date, uniqueFileName);
    }

    public async Task<Stream> LoadAsync(string url)
    {
        var filePath = ResolvePath(url);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found.", filePath);

        return new FileStream(filePath, FileMode.Open, FileAccess.Read);
    }

    public async Task RemoveAsync(string url)
    {
        var fullPath = ResolvePath(url);

        if (!File.Exists(fullPath))
            return;

        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.IsReadOnly)
                fileInfo.IsReadOnly = false;

            File.Delete(fullPath);
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed to delete file: {fullPath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"No access to delete file: {fullPath}", ex);
        }

        await Task.CompletedTask;
    }

    private string ResolvePath(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Invalid file url", nameof(url));
        if (Path.IsPathRooted(url)) throw new UnauthorizedAccessException("Invalid file path");
        var fullPath = Path.GetFullPath(Path.Combine(_storagePath, url));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(_storagePath + Path.DirectorySeparatorChar, comparison))
            throw new UnauthorizedAccessException("Invalid file path");
        return fullPath;
    }
}
