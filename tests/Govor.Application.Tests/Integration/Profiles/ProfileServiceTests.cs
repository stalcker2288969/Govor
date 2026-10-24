using Govor.Application.Profiles;
using Govor.Domain.Common;
using Govor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Govor.Application.Tests.Profiles;

[TestFixture]
public class ProfileServiceTests
{
    private TestDatabase _db = null!;
    private ProfileService _service = null!;
    [SetUp] public void SetUp() { _db = new TestDatabase(); _service = new(_db.Context, NullLogger<ProfileService>.Instance); }
    [TearDown] public void TearDown() => _db.Dispose();

    [Test]
    public async Task ReadAndUpdateDescriptionPersistWithoutChangingOtherUsers()
    {
        Assert.That((await _service.SetDescription("new description", _db.Alice)).IsSuccess, Is.True);
        var profile = (await _service.GetUserProfileAsync(_db.Alice)).Value;
        Assert.That(profile.Id, Is.EqualTo(_db.Alice));
        Assert.That(profile.Description, Is.EqualTo("new description"));
        Assert.That((await _service.GetUserProfileAsync(_db.Bob)).Value.Description, Is.Empty);
    }

    [Test]
    public async Task MissingUserReturnsNotFound()
    {
        Assert.That((await _service.GetUserProfileAsync(Guid.NewGuid())).Error.Type, Is.EqualTo(ErrorType.NotFound));
        Assert.That((await _service.SetDescription("test", Guid.NewGuid())).Error.Type, Is.EqualTo(ErrorType.NotFound));
    }

    [TestCase(true, MediaOwnerType.Avatar, true)]
    [TestCase(false, MediaOwnerType.Avatar, false)]
    [TestCase(true, MediaOwnerType.Message, false)]
    public async Task IconMustBeOwnUploadedAvatar(bool owned, MediaOwnerType type, bool success)
    {
        var media = new MediaFile { Id = Guid.NewGuid(), UploaderId = owned ? _db.Alice : _db.Bob,
            Url = "avatar.png", MineType = "image/png", OwnerType = type };
        _db.Context.MediaFiles.Add(media);
        await _db.Context.SaveChangesAsync();
        Assert.That((await _service.SetNewIcon(_db.Alice, media.Id)).IsSuccess, Is.EqualTo(success));
        Assert.That((await _service.GetUserProfileAsync(_db.Alice)).Value.IconId,
            Is.EqualTo(success ? media.Id : Guid.Empty));
    }

    [Test]
    public async Task AvatarOfDeletedAccountDoesNotPermitUpdatingMissingProfile()
    {
        var media = new MediaFile { Id = Guid.NewGuid(), UploaderId = _db.Outsider, Url = "avatar.png",
            MineType = "image/png", OwnerType = MediaOwnerType.Avatar };
        _db.Context.MediaFiles.Add(media);
        await _db.Context.SaveChangesAsync();
        await _db.Context.Users.Where(u => u.Id == _db.Outsider).ExecuteDeleteAsync();
        Assert.That((await _service.SetNewIcon(_db.Outsider, media.Id)).Error.Type, Is.EqualTo(ErrorType.NotFound));
    }

    [Test]
    public async Task UnknownMediaCannotBeUsedAsIcon() =>
        Assert.That((await _service.SetNewIcon(_db.Alice, Guid.NewGuid())).Error.Type, Is.EqualTo(ErrorType.Forbidden));
}
