using Govor.Application.Medias;
using Govor.Domain.Models;
using Govor.Domain.Models.Messages;
using Microsoft.EntityFrameworkCore;

namespace Govor.Application.Tests.Medias;

[TestFixture]
public class MediaAccessTests
{
    private TestDatabase _db = null!;
    private AccesserToDownloadMediaService _service = null!;
    [SetUp] public void SetUp() { _db = new TestDatabase(); _service = new(_db.Context); }
    [TearDown] public void TearDown() => _db.Dispose();

    private async Task<MediaFile> AddMedia(MediaOwnerType type, Guid? owner = null)
    {
        var media = new MediaFile { Id = Guid.NewGuid(), UploaderId = _db.Alice, Url = "test.bin",
            MineType = "application/octet-stream", OwnerType = type, OwnerId = owner, DateCreated = DateTime.UtcNow };
        _db.Context.MediaFiles.Add(media);
        await _db.Context.SaveChangesAsync();
        return media;
    }

    [Test]
    public async Task MessageAttachmentIsAvailableOnlyToChatParticipants()
    {
        var message = _db.AddMessage();
        var media = await AddMedia(MediaOwnerType.Message, message.Id);
        _db.Context.MediaAttachments.Add(new MediaAttachments { Id = Guid.NewGuid(), MessageId = message.Id, MediaFileId = media.Id });
        await _db.Context.SaveChangesAsync();
        Assert.That(await _service.HasAccessAsync(media.Id, _db.Alice), Is.True);
        Assert.That(await _service.HasAccessAsync(media.Id, _db.Bob), Is.True);
        Assert.That(await _service.HasAccessAsync(media.Id, _db.Outsider), Is.False);
    }

    [TestCase(MediaOwnerType.Avatar)]
    [TestCase(MediaOwnerType.System)]
    public async Task PublicMediaCanBeDownloaded(MediaOwnerType type) =>
        Assert.That(await _service.HasAccessAsync((await AddMedia(type)).Id, _db.Outsider), Is.True);

    [TestCase(MediaOwnerType.Message)]
    [TestCase(MediaOwnerType.Reaction)]
    [TestCase((MediaOwnerType)99)]
    public async Task UnattachedMediaCannotBeDownloaded(MediaOwnerType type) =>
        Assert.That(await _service.HasAccessAsync((await AddMedia(type)).Id, _db.Alice), Is.False);

    [Test]
    public async Task UnknownMediaCannotBeDownloaded() =>
        Assert.That(await _service.HasAccessAsync(Guid.NewGuid(), _db.Alice), Is.False);

    [TestCase(false)]
    [TestCase(true)]
    public async Task GroupAvatarRequiresMembershipForPrivateGroupAndRejectsBannedMembers(bool isPrivate)
    {
        var group = new ChatGroup { Id = Guid.NewGuid(), Name = "group", Description = "", IsPrivate = isPrivate };
        _db.Context.ChatGroups.Add(group);
        var member = new GroupMembership { Id = Guid.NewGuid(), GroupId = group.Id, UserId = _db.Bob };
        _db.Context.GroupMemberships.Add(member);
        await _db.Context.SaveChangesAsync();
        var media = await AddMedia(MediaOwnerType.GroupAvatar, group.Id);
        group.ImageId = media.Id;
        await _db.Context.SaveChangesAsync();
        Assert.That(await _service.HasAccessAsync(media.Id, _db.Bob), Is.True);
        Assert.That(await _service.HasAccessAsync(media.Id, _db.Outsider), Is.EqualTo(!isPrivate));
        member.IsBanned = true;
        await _db.Context.SaveChangesAsync();
        Assert.That(await _service.HasAccessAsync(media.Id, _db.Bob), Is.False);
    }
}
