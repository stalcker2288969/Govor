using Govor.API.Controllers.Authentication;
using Govor.Application.Friends;
using Govor.Application.Infrastructure.Extensions;
using Govor.Application.Users.UserSessions.Crypto;
using Govor.Contracts.Requests;
using Govor.Contracts.DTOs;
using Govor.Domain.Models.Users;
using Govor.Domain.Models.Users.Crypto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.API.Tests.Unit.Controllers;

[TestFixture]
public class SessionKeysControllerTests
{
    private readonly Guid _user = Guid.NewGuid(), _session = Guid.NewGuid(), _friend = Guid.NewGuid();
    private Mock<IFriendshipService> _friends = null!;
    private Mock<ISessionKeyAttacher> _attacher = null!;
    private Mock<ISessionKeysReader> _reader = null!;
    private Mock<IOneTimePreKeysRotator> _rotator = null!;
    private SessionKeysController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _friends = new(); _attacher = new(); _reader = new(); _rotator = new();
        var user = new Mock<ICurrentUserService>(); user.Setup(x => x.GetCurrentUserId()).Returns(_user);
        var session = new Mock<ICurrentUserSessionService>(); session.Setup(x => x.GetUserSessionId()).Returns(_session);
        _controller = new(NullLogger<SessionKeysController>.Instance, _friends.Object, session.Object, _attacher.Object, _reader.Object, user.Object, _rotator.Object);
    }

    [TestCase(false, 0)] [TestCase(false, 100)] [TestCase(false, 101)] [TestCase(true, 1)]
    public async Task UploadValidatesModelAndKeyCount(bool invalidModel, int count)
    {
        var request = new UploadKeysRequest { IdentityKey = [1], SignedPreKey = [2], SignedPreKeySignature = [3], OneTimePreKeys = Enumerable.Range(0,count).Select(_ => new byte[] {4}).ToList() };
        if (invalidModel) _controller.ModelState.AddModelError("keys", "invalid");
        var result = await _controller.UploadSessionKeys(request);
        if (invalidModel || count > 100) { Assert.That(result, Is.TypeOf<BadRequestObjectResult>()); _attacher.VerifyNoOtherCalls(); }
        else {
            Assert.That(result, Is.TypeOf<OkResult>());
            _attacher.Verify(x => x.AttachKeysAsync(_session, request.IdentityKey, request.SignedPreKey, request.SignedPreKeySignature, request.OneTimePreKeys), Times.Once);
        }
    }

    [TestCase(false, 0)] [TestCase(false, 100)] [TestCase(false, 101)] [TestCase(true, 1)]
    public async Task RotationValidatesAndUsesAuthenticatedSession(bool invalidModel, int count)
    {
        var request = new RotateOneTimePreKeysRequest { NewOneTimePreKeys = Enumerable.Range(0,count).Select(_ => new byte[] {4}).ToList() };
        if (invalidModel) _controller.ModelState.AddModelError("keys", "invalid");
        var result = await _controller.RotateOneTimePreKeys(request);
        if (invalidModel || count > 100) { Assert.That(result, Is.TypeOf<BadRequestObjectResult>()); _rotator.VerifyNoOtherCalls(); }
        else { Assert.That(result, Is.TypeOf<OkObjectResult>()); _rotator.Verify(x => x.RotateOneTimePreKeysAsync(_session, request.NewOneTimePreKeys), Times.Once); }
    }

    [Test]
    public async Task NonFriendCannotReadKeys()
    {
        _friends.Setup(x => x.GetFriendsAsync(_friend)).ReturnsAsync([]);
        Assert.That(await _controller.GetUserPublicKeys(_friend), Is.TypeOf<ForbidResult>());
        _reader.VerifyNoOtherCalls();
    }

    [Test]
    public async Task FriendKeysAreBase64AndUsedPrekeysAreExcluded()
    {
        _friends.Setup(x => x.GetFriendsAsync(_friend)).ReturnsAsync([new User { Id = _user }]);
        var unusedId = Guid.NewGuid();
        _reader.Setup(x => x.GetAllActiveKeysAsync(_friend)).ReturnsAsync([
            new UserCryptoSession { PublicIdentityKey = [1], SignedPreKey = new() { Id = Guid.NewGuid(), PublicSignedPreKey = [2], SignedPreKeySignature = [3] }, OneTimePreKeys = [new() {Id = unusedId, PublicKey = [4]}, new() {Id = Guid.NewGuid(), PublicKey = [5], IsUsed = true}] },
            new UserCryptoSession { PublicIdentityKey = [6], SignedPreKey = null!, OneTimePreKeys = [] }
        ]);
        var dtos = ((IEnumerable<PublicSessionKeysDto>)((OkObjectResult)await _controller.GetUserPublicKeys(_friend)).Value!).ToList();
        Assert.Multiple(() => {
            Assert.That(dtos[0].IdentityKey, Is.EqualTo("AQ==")); Assert.That(dtos[0].SignedPreKey!.Key, Is.EqualTo("Ag=="));
            Assert.That(dtos[0].SignedPreKey!.Signature, Is.EqualTo("Aw==")); Assert.That(dtos[0].OneTimePreKeys.Single().Id, Is.EqualTo(unusedId));
            Assert.That(dtos[0].OneTimePreKeys.Single().Key, Is.EqualTo("BA==")); Assert.That(dtos[1].SignedPreKey, Is.Null);
        });
    }

    [Test]
    public async Task CountAndConsumptionUseAuthenticatedSession()
    {
        _reader.Setup(x => x.GetRemainingOneTimePreKeysCountAsync(_session)).ReturnsAsync(17);
        var result = (OkObjectResult)await _controller.GetRemainingOneTimePreKeysCount();
        Assert.That(result.Value!.GetType().GetProperty("remaining")!.GetValue(result.Value), Is.EqualTo(17));
        var key = Guid.NewGuid();
        Assert.That(await _controller.MarkOneTimePreKeyAsUsed(key), Is.TypeOf<OkObjectResult>());
        _rotator.Verify(x => x.MarkOneTimePreKeyAsUsedAsync(_session, key), Times.Once);
    }
}
