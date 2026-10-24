using Govor.Application.Groups;
using Govor.Application.Infrastructure.Common;
using Govor.Application.Reactions;
using Govor.Application.Tests.Support;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Govor.Domain.Models.Reactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Application.Tests.Integration.Groups;

[TestFixture]
public class GroupAvatarBoundaryTests
{
    private TestDatabase _db = null!;
    private Guid _group;
    private Mock<IReactionMediaProcessor> _processor = null!;
    private Mock<IStorageService> _storage = null!;
    private readonly ProcessedReactionMedia _image = new(new byte[] { 1 }, ReactionKind.Image, "image/png", ".png", 10, 10, 0);
    private GroupAvatarService Service(GovorDbContext? context = null) => new(context ?? _db.Context,
        Mock.Of<INowDateTimeProvider>(c => c.Now == DateTime.UtcNow.Date), _processor.Object, _storage.Object, NullLogger<GroupAvatarService>.Instance);
    [SetUp]
    public async Task SetUp()
    {
        _db = new TestDatabase();
        _group = Guid.NewGuid();
        _db.Context.ChatGroups.Add(new ChatGroup { Id = _group, Name = "group", Description = "", OwnerUserId = _db.Alice });
        _db.Context.GroupMemberships.Add(new GroupMembership { Id = Guid.NewGuid(), GroupId = _group, UserId = _db.Alice });
        await _db.Context.SaveChangesAsync();
        _processor = new Mock<IReactionMediaProcessor>(MockBehavior.Strict);
        _storage = new Mock<IStorageService>(MockBehavior.Strict);
    }
    [TearDown] public void TearDown() => _db.Dispose();

    [Test]
    public async Task NonOwnerCannotProcessSaveOrRemoveAvatar()
    {
        Assert.That((await Service().UploadAsync(_db.Bob, _group, new byte[] { 1 })).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        Assert.That((await Service().RemoveAsync(_db.Bob, _group)).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        _processor.VerifyNoOtherCalls();
        _storage.VerifyNoOtherCalls();
    }

    [Test]
    public async Task InvalidAndAnimatedImagesAreRejectedBeforeStorage()
    {
        var error = Error.Validation("Media.Invalid", "invalid");
        _processor.SetupSequence(p => p.ProcessAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<ProcessedReactionMedia>(error)).ReturnsAsync(_image with { Kind = ReactionKind.Gif });
        Assert.That((await Service().UploadAsync(_db.Alice, _group, new byte[] { 1 })).Error, Is.SameAs(error));
        Assert.That((await Service().UploadAsync(_db.Alice, _group, new byte[] { 1 })).Error.Code, Is.EqualTo("Group.Avatar.Invalid"));
        _storage.VerifyNoOtherCalls();
    }

    [Test]
    public async Task LossOfOwnershipDuringImageProcessingPreventsUpload()
    {
        _processor.Setup(p => p.ProcessAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).Returns(async () =>
        {
            await _db.Context.ChatGroups.Where(g => g.Id == _group).ExecuteUpdateAsync(s => s.SetProperty(g => g.OwnerUserId, _db.Bob));
            return Result.Success(_image);
        });
        Assert.That((await Service().UploadAsync(_db.Alice, _group, new byte[] { 1 })).Error.Type, Is.EqualTo(ErrorType.Forbidden));
        _storage.VerifyNoOtherCalls();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedMetadataWriteRollsBackAndCleansStoredImage(bool cleanupFails)
    {
        _processor.Setup(p => p.ProcessAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(_image);
        _storage.Setup(s => s.SaveAsync(It.IsAny<byte[]>(), It.IsAny<string>())).ReturnsAsync("failed.png");
        if (cleanupFails) _storage.Setup(s => s.RemoveAsync("failed.png")).ThrowsAsync(new IOException("cleanup failure"));
        else _storage.Setup(s => s.RemoveAsync("failed.png")).Returns(Task.CompletedTask);
        using var failed = _db.CreateContext(new FailDatabaseCommand(sql => sql.Contains("INSERT INTO \"MediaFiles\"", StringComparison.Ordinal)));
        Assert.ThrowsAsync<DbUpdateException>(() => Service(failed).UploadAsync(_db.Alice, _group, new byte[] { 1 }));
        _storage.Verify(s => s.RemoveAsync("failed.png"), Times.Once);
        Assert.That(await _db.Context.MediaFiles.CountAsync(), Is.Zero);
        Assert.That((await _db.Context.ChatGroups.AsNoTracking().SingleAsync()).ImageId, Is.EqualTo(Guid.Empty));
        Assert.That(failed.ChangeTracker.Entries(), Is.Empty);
    }
}

