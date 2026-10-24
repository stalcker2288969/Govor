using System.Text;
using AutoMapper;
using Govor.API.Controllers;
using Govor.API.Hubs;
using Govor.Application.Profiles;
using Govor.Contracts.DTOs;
using Govor.Contracts.Requests;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;

namespace Govor.API.Tests;

[TestFixture]
public class ProfileControllerTests
{
private Mock<ILogger<ProfileController>> _mockLogger = null!;
    private Mock<IMediaService> _mockMediaService = null!;
    private Mock<IProfileService> _mockProfileService = null!;
    private Mock<ICurrentUserService> _mockCurrentUserService = null!;
    private Mock<IHubContext<ProfileHub>> _mockProfileHubContext = null!;
    private Mock<IHubClients> _mockClients = null!;
    private Mock<IClientProxy> _mockClientProxy = null!;
    private Mock<IMapper> _mockMapper = null!;
    private ProfileController _controller = null!;

    private Guid _userId;
    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<ProfileController>>();
        _mockMediaService = new Mock<IMediaService>();
        _mockProfileService = new Mock<IProfileService>();
        _mockCurrentUserService = new Mock<ICurrentUserService>();
        _mockMapper = new Mock<IMapper>();
        
        _mockProfileHubContext = new Mock<IHubContext<ProfileHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        
        _mockProfileHubContext.Setup(x => x.Clients).Returns(_mockClients.Object);
        _mockClients.Setup(x => x.All).Returns(_mockClientProxy.Object);
        _mockClientProxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _controller = new ProfileController(
            _mockLogger.Object,
            _mockMediaService.Object,
            _mockProfileService.Object,
            _mockCurrentUserService.Object,
            _mockProfileHubContext.Object, 
            _mockMapper.Object);

        _userId = Guid.NewGuid();
        _mockCurrentUserService.Setup(x => x.GetCurrentUserId()).Returns(_userId);
    }

    [Test]
    public async Task UploadAvatar_ReturnsBadRequest_WhenFileIsEmpty()
    {
        // Arrange
        var request = new AvatarUploadRequest
        {
            FromFile = null 
        };

        // Act
        var result = await _controller.UploadAvatar(request);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequestResult = (BadRequestObjectResult)result;
        Assert.That(badRequestResult.Value, Is.EqualTo("File is empty."));
        
        _mockMediaService.Verify(s => s.UploadMediaAsync(It.IsAny<Media>()), Times.Never);
    }
    
    [Test]
    public async Task UploadAvatar_ReturnsBadRequest_WhenFileLengthIsZero()
    {
        // Arrange
        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.Length).Returns(0);

        var request = new AvatarUploadRequest
        {
            FromFile = mockFile.Object
        };

        // Act
        var result = await _controller.UploadAvatar(request);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequestResult = (BadRequestObjectResult)result;
        Assert.That(badRequestResult.Value, Is.EqualTo("File is empty."));
        
        _mockMediaService.Verify(s => s.UploadMediaAsync(It.IsAny<Media>()), Times.Never);
    }

    [Test]
    public async Task UploadAvatar_ReturnsOkAndPerformsAllActions_WhenFileIsValid()
    {
        // Arrange
        var mediaId = Guid.NewGuid();
        var expectedMediaInfo = new MediaUploadResult(mediaId, $"/media/{mediaId}");
        
        var fileName = "test_avatar.jpg";
        var fileContent = "This is a test image content";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent));
        
        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.FileName).Returns(fileName);
        mockFile.Setup(f => f.Length).Returns(stream.Length);
        mockFile.Setup(f => f.OpenReadStream()).Returns(stream);
        mockFile.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, CancellationToken>((s, t) => stream.CopyTo(s)); // Для ReadFileAsync

        var request = new AvatarUploadRequest
        {
            FromFile = mockFile.Object,
            Type = MediaType.Image,
            MimeType = "image/jpeg"
        };
        
        _mockMediaService.Setup(s => s.UploadMediaAsync(It.IsAny<Media>()))
            .ReturnsAsync(expectedMediaInfo);

        _mockProfileService.Setup(s => s.SetNewIcon(_userId, mediaId))
            .ReturnsAsync(Govor.Domain.Common.Result.Success());

        // Act
        var result = await _controller.UploadAvatar(request);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        Assert.That(okResult.StatusCode, Is.EqualTo(200));
        Assert.That(okResult.Value, Is.EqualTo(expectedMediaInfo));
        
        mockFile.Verify(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        
        _mockMediaService.Verify(s => s.UploadMediaAsync(It.Is<Media>(m => 
            m.OwnerId == _userId && 
            m.OwnerType == MediaOwnerType.Avatar &&
            m.FileName == fileName
        )), Times.Once);
        
        _mockProfileService.Verify(s => s.SetNewIcon(_userId, mediaId), Times.Once);
    }
    
    [Test]
    public async Task UploadAvatar_ReturnsInternalServerError_WhenMediaServiceFails()
    {
        // Arrange
        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.Length).Returns(100);
        mockFile.Setup(f => f.OpenReadStream()).Returns(new MemoryStream(Encoding.UTF8.GetBytes("test")));
        
        var request = new AvatarUploadRequest { FromFile = mockFile.Object };
        
        _mockMediaService.Setup(s => s.UploadMediaAsync(It.IsAny<Media>()))
            .ThrowsAsync(new System.Exception("Simulated upload failure"));

        // Act
        var result = await _controller.UploadAvatar(request);

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = (ObjectResult)result;
        Assert.That(objectResult.StatusCode, Is.EqualTo(500));
        
        _mockProfileService.Verify(s => s.SetNewIcon(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        
        _mockClientProxy.Verify(
            c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "The Hub should not be called in case of a service error."
        );
    }
    
    [Test]
    public async Task DowloadProfile_ReturnsOk_WhenProfileExists()
    {
        // Arrange
        var userProfile = new UserProfile
        {
            Id = _userId,
            Username = "test_user",
            Description = "test description",
            IconId = Guid.NewGuid()
        };

        var userProfileDto = new UserProfileDto
        {
            Id = _userId,
            Username = "test_user",
            Description = "test description",
            IconId = userProfile.IconId
        };

        _mockProfileService.Setup(s => s.GetUserProfileAsync(_userId))
            .ReturnsAsync(userProfile);

        _mockMapper.Setup(m => m.Map<UserProfileDto>(userProfile))
            .Returns(userProfileDto);

        // Act
        var result = await _controller.DownloadProfile();

        // Assert
        var okResult = result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        Assert.That(okResult!.StatusCode, Is.EqualTo(200));
        Assert.That(okResult.Value, Is.EqualTo(userProfileDto));
    }

    [Test]
    public async Task DowloadProfile_ReturnsForbid_WhenUnauthorizedAccessExceptionThrown()
    {
        // Arrange
        _mockProfileService.Setup(s => s.GetUserProfileAsync(_userId))
            .ThrowsAsync(new UnauthorizedAccessException("Access denied"));

        // Act
        var result = await _controller.DownloadProfile();

        // Assert
        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task DownloadProfile_MissingProfile_Returns404ProblemDetails()
    {
        // Arrange
        _mockProfileService.Setup(s => s.GetUserProfileAsync(_userId))
            .ReturnsAsync(Govor.Domain.Common.Result.Failure<UserProfile>(
                Govor.Domain.Common.Error.NotFound("Profile.UserNotFound", "Cannot find profile")));

        // Act
        var result = await _controller.DownloadProfile();

        // Assert
        var response = (ObjectResult)result;
        Assert.That(response.StatusCode, Is.EqualTo(404));
        Assert.That(((ProblemDetails)response.Value!).Extensions["errorCode"], Is.EqualTo("Profile.UserNotFound"));
    }

    [Test]
    public async Task DowloadProfile_Returns500_WhenUnexpectedExceptionThrown()
    {
        // Arrange
        _mockProfileService.Setup(s => s.GetUserProfileAsync(_userId))
            .ThrowsAsync(new Exception("Unexpected error"));

        // Act
        var result = await _controller.DownloadProfile();

        // Assert
        var objectResult = result as ObjectResult;
        Assert.That(objectResult, Is.Not.Null);
        Assert.That(objectResult!.StatusCode, Is.EqualTo(500));
        Assert.That(objectResult.Value, Is.EqualTo("Unexpected Error! Please try again later."));
    }
}
