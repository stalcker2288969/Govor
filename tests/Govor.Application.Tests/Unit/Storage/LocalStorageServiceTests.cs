using AutoFixture;
using Microsoft.AspNetCore.Hosting;
using Moq;

namespace Govor.Application.Tests.Services;

[TestFixture]
public class LocalStorageServiceTests
{
    private Fixture _fixture = null!;
    private IStorageService _service = null!;
    private Mock<IWebHostEnvironment> _webHostEnvironmentMock = null!;
    private string _tempDirectory = null!;
    [SetUp]
    public void SetUp()
    {
        _fixture = new Fixture();
        _webHostEnvironmentMock = new Mock<IWebHostEnvironment>();
        
        _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);

        _webHostEnvironmentMock.Setup(w => w.ContentRootPath).Returns(_tempDirectory);

        _service = new LocalStorageService(_webHostEnvironmentMock.Object.ContentRootPath);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }
    
    [Test]
    public async Task Given_ValidDataAndFileName_When_SaveAsync_Then_FileIsSaved()
    {
        // Arrange
        var data = _fixture.CreateMany<byte>(1000).ToArray();
        var fileName = _fixture.Create<string>() + ".txt";
        var saveDate = DateTime.UtcNow;

        // Act
        var url = await _service.SaveAsync(data, fileName);

        // Assert
        Assert.That(url, Is.Not.Null);

        // _tempDirectory/uploads/yyyy/MM/url
        var datePath = saveDate.ToString("yyyy/MM");
        var expectedFilePath = Path.Combine(_tempDirectory, "uploads", url);
        Assert.That(File.Exists(expectedFilePath), Is.True);
    }
    
    [Test]
    public void Given_EmptyName_When_SaveAsync_Should_Throw_ArgumentException()
    {
        // Arrange 
        var data = _fixture.CreateMany<byte>(1000).ToArray();
        
        // Act & Assert 
        Assert.ThrowsAsync<ArgumentException>(async () => await _service.SaveAsync(data, string.Empty));
    }

    [Test]
    public void Given_EmptyData_When_SaveAsync_Should_Throw_ArgumentException()
    {
        // Arrange 
        var name = _fixture.Create<string>();
        
        // Act & Assert 
        Assert.ThrowsAsync<ArgumentException>(async () => await _service.SaveAsync(default, name));
    }
    
    [Test]
    public async Task Given_ValidUrl_When_LoadAsync_Then_Returns_FileStream()
    {
        // Arrange
        var date = DateTime.UtcNow.ToString("yyyy/MM");
        var corePath = Path.Combine(_tempDirectory, "uploads", date);
        Directory.CreateDirectory(corePath);
        
        var fileName = _fixture.Create<string>() + ".txt";
        var fileContent = _fixture.Create<string>();
        var filePath = Path.Combine(corePath, fileName);
        await File.WriteAllTextAsync(filePath, fileContent);

        // Act
        var url = Path.Combine(date, fileName);
        await using var stream = await _service.LoadAsync(url);

        // Assert
        Assert.That(stream, Is.Not.Null);
        Assert.That(stream.Length, Is.GreaterThan(0));
    
        // Дополнительная проверка: содержимое файла
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        Assert.That(content, Is.EqualTo(fileContent));
    }
    
    [Test]
    public void Given_EmptyUrl_When_LoadAsync_Should_Throw_FileNotFoundException()
    {
        // Act & Assert 
        Assert.ThrowsAsync<FileNotFoundException>(async () => await _service.LoadAsync(_fixture.Create<string>()+".txt"));
    }

    [Test]
    public async Task Given_ValidUrl_When_DeleteAsync_Then_FileIsDeleted()
    {
        // Arrange
        var date = DateTime.UtcNow.ToString("yyyy/MM");
        var corePath = Path.Combine(_tempDirectory, "uploads", date);
        Directory.CreateDirectory(corePath);
        
        var fileName = _fixture.Create<string>() + ".txt";
        var fileContent = _fixture.Create<string>();
        var filePath = Path.Combine(corePath, fileName);
        await File.WriteAllTextAsync(filePath, fileContent);
        
        // Act
        var url = Path.Combine(date, fileName);
        await _service.RemoveAsync(url);
        
        Assert.That(File.Exists(filePath), Is.False);
    }
    
    [Test]
    public async Task Given_InvalidUrl_When_DeleteAsync_TShould_Not_Throw()
    {
        // Act & Assert 
        Assert.DoesNotThrowAsync(async () => await _service.RemoveAsync(_fixture.Create<string>()+".txt"));
    }
}
