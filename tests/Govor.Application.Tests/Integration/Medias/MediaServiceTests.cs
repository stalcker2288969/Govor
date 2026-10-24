using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Application.Tests.Integration.Medias;

[TestFixture]
public class MediaServiceTests
{
    private TestDatabase _db = null!;
    private Mock<IStorageService> _storage = null!;
    private MediaService _service = null!;
    [SetUp]
    public void SetUp()
    {
        _db = new TestDatabase();
        _storage = new Mock<IStorageService>();
        _storage.Setup(s => s.SaveAsync(It.IsAny<byte[]>(), It.IsAny<string>())).ReturnsAsync("folder/test.bin");
        _storage.Setup(s => s.LoadAsync("folder/test.bin")).ReturnsAsync(() => new MemoryStream(new byte[] { 1, 2, 3 }));
        _service = new(_storage.Object, _db.Context, NullLogger<MediaService>.Instance);
    }
    [TearDown] public void TearDown() => _db.Dispose();

    private Task<SmartRes.Result<MediaUploadResult, Error>> Upload(MediaOwnerType type = MediaOwnerType.Message) =>
        _service.UploadMediaAsync(new Media(_db.Alice, DateTime.UtcNow, "test.bin", new byte[] { 1, 2, 3 },
            MediaType.File, "application/octet-stream", "", type, null));

    [Test]
    public async Task UploadReadByIdAndUrlAndDeleteRoundTrip()
    {
        var uploaded = (await Upload()).Value;
        _db.Context.ChangeTracker.Clear();
        Assert.That(await _service.HasMediaAsync(uploaded.MediaId), Is.True);
        Assert.That(await _service.HasMediaByUrlAsync(uploaded.Url), Is.True);
        foreach (var media in new[] { (await _service.GetMediaByIdAsync(uploaded.MediaId)).Value,
            (await _service.GetMediaByUrlAsync(uploaded.Url)).Value })
        {
            Assert.That(media.Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(media.UploaderId, Is.EqualTo(_db.Alice));
            Assert.That(media.FileName, Is.EqualTo("test.bin"));
            Assert.That(media.MimeType, Is.EqualTo("application/octet-stream"));
            Assert.That(media.EncryptedKey, Is.Empty);
        }
        Assert.That((await _service.DeleteMediaAsync(uploaded.MediaId)).IsSuccess, Is.True);
        Assert.That(await _service.HasMediaAsync(uploaded.MediaId), Is.False);
        Assert.That(await _service.HasMediaByUrlAsync(uploaded.Url), Is.False);
        _storage.Verify(s => s.RemoveAsync(uploaded.Url), Times.Once);
    }

    [Test]
    public async Task UnknownMediaOperationsReturnNotFoundAndNeverReadStorage()
    {
        Assert.That((await _service.GetMediaByIdAsync(Guid.NewGuid())).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.GetMediaByUrlAsync("unknown")).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.DeleteMediaAsync(Guid.NewGuid())).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.AttachToMessageAsync(Guid.NewGuid(), Guid.NewGuid())).Error.Type, Is.EqualTo(ErrorType.NotFound));
        _storage.VerifyNoOtherCalls();
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase(null)]
    public async Task BlankUrlReturnsValidationError(string? url) =>
        Assert.That((await _service.GetMediaByUrlAsync(url!)).Error.Type, Is.EqualTo(ErrorType.Validation));

    [Test]
    public async Task InvalidUploadReturnsFailureWithoutMetadata()
    {
        _storage.Setup(s => s.SaveAsync(It.IsAny<byte[]>(), It.IsAny<string>())).ThrowsAsync(new ArgumentException("bad data"));
        Assert.That((await Upload()).IsFailure, Is.True);
        Assert.That(await _db.Context.MediaFiles.CountAsync(), Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingStorageFileReturnsServerError(bool byUrl)
    {
        var uploaded = (await Upload()).Value;
        _storage.Setup(s => s.LoadAsync(uploaded.Url)).ThrowsAsync(new FileNotFoundException());
        var result = byUrl ? await _service.GetMediaByUrlAsync(uploaded.Url) : await _service.GetMediaByIdAsync(uploaded.MediaId);
        Assert.That(result.Error.Type, Is.EqualTo(ErrorType.ServerError));
        Assert.That(await _service.HasMediaAsync(uploaded.MediaId), Is.True);
    }

    [Test]
    public async Task FailedStorageDeletePreservesMetadata()
    {
        var uploaded = (await Upload()).Value;
        _storage.Setup(s => s.RemoveAsync(uploaded.Url)).ThrowsAsync(new IOException("disk failure"));
        Assert.ThrowsAsync<IOException>(() => _service.DeleteMediaAsync(uploaded.MediaId));
        Assert.That(await _service.HasMediaAsync(uploaded.MediaId), Is.True);
    }

    [Test]
    public async Task MessageMediaCanBeAssignedButAvatarCannot()
    {
        var messageMedia = (await Upload()).Value;
        var avatar = (await Upload(MediaOwnerType.Avatar)).Value;
        var message = _db.AddMessage();
        Assert.That((await _service.AttachToMessageAsync(messageMedia.MediaId, message.Id)).IsSuccess, Is.True);
        Assert.That((await _service.AttachToMessageAsync(avatar.MediaId, message.Id)).IsFailure, Is.True);
        _db.Context.ChangeTracker.Clear();
        Assert.That((await _db.Context.MediaFiles.SingleAsync(m => m.Id == messageMedia.MediaId)).OwnerId, Is.EqualTo(message.Id));
        Assert.That((await _db.Context.MediaFiles.SingleAsync(m => m.Id == avatar.MediaId)).OwnerType, Is.EqualTo(MediaOwnerType.Avatar));
    }
}

