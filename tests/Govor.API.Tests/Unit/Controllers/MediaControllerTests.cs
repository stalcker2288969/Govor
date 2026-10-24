using System.ComponentModel.DataAnnotations;
using Govor.API.Controllers;
using Govor.Application.E2ee;
using Govor.Application.Medias;
using Govor.Contracts.Requests;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class MediaControllerTests
{
    private Mock<IMediaService> _media = null!;
    private Mock<IAccesserToDownloadMedia> _access = null!;
    private MediaController _controller = null!;
    private readonly Guid _user = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _media = new Mock<IMediaService>();
        _access = new Mock<IAccesserToDownloadMedia>();
        var current = new Mock<ICurrentUserService>();
        current.Setup(c => c.GetCurrentUserId()).Returns(_user);
        _controller = new(NullLogger<MediaController>.Instance, _media.Object, _access.Object, current.Object);
    }

    private static MediaUploadRequest Request(string mime = E2eeProtocol.EncryptedMimeType, string key = "") =>
        new() { FromFile = new FormFile(new MemoryStream(new byte[] { 1, 2, 3 }), 0, 3, "file", "encrypted.bin"),
            Type = MediaType.File, MimeType = mime, EncryptedKey = key, OwnerType = MediaOwnerType.Message };

    [Test]
    public async Task EncryptedUploadPassesCiphertextWithoutKeysToService()
    {
        var upload = new MediaUploadResult(Guid.NewGuid(), "stored.bin");
        _media.Setup(m => m.UploadMediaAsync(It.IsAny<Media>())).ReturnsAsync(upload);
        var request = Request();
        var validation = new List<ValidationResult>();
        Assert.That(Validator.TryValidateObject(request, new ValidationContext(request), validation, true),
            Is.True, string.Join("; ", validation.Select(v => v.ErrorMessage)));
        Assert.That(((OkObjectResult)await _controller.Upload(request)).Value, Is.SameAs(upload));
        _media.Verify(m => m.UploadMediaAsync(It.Is<Media>(data => data.UploaderId == _user &&
            data.EncryptedKey == "" && data.Data.SequenceEqual(new byte[] { 1, 2, 3 }) &&
            data.MimeType == E2eeProtocol.EncryptedMimeType && data.OwnerId == null)), Times.Once);
    }

    [TestCase("image/png", "")]
    [TestCase(E2eeProtocol.EncryptedMimeType, "decryption-secret")]
    public async Task PlaintextAttachmentOrLeakedKeyIsRejected(string mime, string key)
    {
        Assert.That(await _controller.Upload(Request(mime, key)), Is.TypeOf<BadRequestObjectResult>());
        _media.VerifyNoOtherCalls();
    }

    [TestCase(MediaOwnerType.System)]
    [TestCase(MediaOwnerType.GroupAvatar)]
    [TestCase(MediaOwnerType.Reaction)]
    public async Task UnsupportedOwnerIsRejected(MediaOwnerType type)
    {
        var request = Request();
        request.OwnerType = type;
        Assert.That(await _controller.Upload(request), Is.TypeOf<BadRequestObjectResult>());
        _media.VerifyNoOtherCalls();
    }

    [Test]
    public async Task MissingFileAndInvalidModelAreRejected()
    {
        var request = Request();
        request.FromFile = null!;
        Assert.That(await _controller.Upload(request), Is.TypeOf<BadRequestObjectResult>());
        _controller.ModelState.AddModelError("file", "invalid");
        Assert.That(await _controller.Upload(Request()), Is.TypeOf<BadRequestObjectResult>());
        _media.VerifyNoOtherCalls();
    }

    [Test]
    public async Task DownloadDeniesAccessBeforeLoadingFile()
    {
        Assert.That(await _controller.Download(Guid.NewGuid()), Is.TypeOf<ForbidResult>());
        _media.VerifyNoOtherCalls();
    }

    [Test]
    public async Task DownloadReturnsBytesAndSanitizesFileName()
    {
        var id = Guid.NewGuid();
        _access.Setup(a => a.HasAccessAsync(id, _user)).ReturnsAsync(true);
        _media.Setup(m => m.GetMediaByIdAsync(id)).ReturnsAsync(new Media(_user, DateTime.UtcNow,
            "folder/encrypted.bin", new byte[] { 1, 2 }, MediaType.File, E2eeProtocol.EncryptedMimeType, "", MediaOwnerType.Message, null));
        var response = (FileContentResult)await _controller.Download(id);
        Assert.That(response.FileContents, Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(response.FileDownloadName, Is.EqualTo("encrypted.bin"));
        Assert.That(response.ContentType, Is.EqualTo(E2eeProtocol.EncryptedMimeType));
    }

    [TestCase(ErrorType.NotFound, 404)]
    [TestCase(ErrorType.ServerError, 500)]
    public async Task DownloadServiceFailureReturnsProblemDetails(ErrorType type, int status)
    {
        var id = Guid.NewGuid();
        _access.Setup(a => a.HasAccessAsync(id, _user)).ReturnsAsync(true);
        _media.Setup(m => m.GetMediaByIdAsync(id)).ReturnsAsync(Result.Failure<Media>(new Error("Media.Failed", "failure", type)));
        Assert.That(((ObjectResult)await _controller.Download(id)).StatusCode, Is.EqualTo(status));
    }
}
