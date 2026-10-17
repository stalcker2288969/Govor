using Govor.Application.Interfaces.PushNotifications.Models;
using Govor.Application.PushNotifications;
using Govor.Application.PushNotifications.Providers;
using Govor.Domain.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartRes;

namespace Govor.Server.Tests;

[TestFixture]
public class PushDeliveryTests
{
    private Mock<IPushTokenService> _tokens = null!;
    private Mock<IPushNotificationProvider> _provider = null!;
    private PushNotificationService _service = null!;
    private Guid _user;

    [SetUp]
    public void SetUp()
    {
        _user = Guid.NewGuid();
        _tokens = new Mock<IPushTokenService>(MockBehavior.Strict);
        _provider = new Mock<IPushNotificationProvider>(MockBehavior.Strict);
        _service = new PushNotificationService(_provider.Object, _tokens.Object,
            NullLogger<PushNotificationService>.Instance);
    }

    [Test]
    public async Task MulticastDeduplicatesAndContinuesAfterOneBatchFails()
    {
        var tokens = Enumerable.Range(0, 501).Select(i => $"token-{i}").ToList();
        _tokens.Setup(t => t.GetStringsActiveTokensAsync(_user))
            .ReturnsAsync(Result<List<string>, Error>.Success(tokens.Concat(tokens).ToList()));
        _provider.Setup(p => p.SendMulticastAsync(It.Is<IReadOnlyList<string>>(t => t.Count == 500), It.IsAny<PushMessage>()))
            .ThrowsAsync(new HttpRequestException("Provider unavailable"));
        _provider.Setup(p => p.SendMulticastAsync(It.Is<IReadOnlyList<string>>(t => t.Count == 1 && t[0] == "token-500"), It.IsAny<PushMessage>()))
            .ReturnsAsync(new SendPushResult(1, 0, []));

        var result = await _service.SendToUserAsync(_user, "title", "body", "chat_messages");

        Assert.That(result.IsFailure, Is.True);
        _provider.VerifyAll();
        _tokens.Verify(t => t.RemoveTokensAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [TestCase(1, true)]
    [TestCase(2, false)]
    public async Task OnlyUnregisteredTokensAreRemovedAndTemporaryFailuresRequireRetry(int failures, bool success)
    {
        _tokens.Setup(t => t.GetStringsActiveTokensAsync(_user))
            .ReturnsAsync(Result<List<string>, Error>.Success(["good", "unregistered", "temporary"]));
        _provider.Setup(p => p.SendMulticastAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<PushMessage>()))
            .ReturnsAsync(new SendPushResult(3 - failures, failures, ["unregistered"]));
        _tokens.Setup(t => t.RemoveTokensAsync(It.Is<IEnumerable<string>>(t => t.SequenceEqual(new[] { "unregistered" }))))
            .ReturnsAsync(Govor.Domain.Common.Result.Success());

        var result = await _service.SendToUserAsync(_user, "title", "body", "chat_messages");

        Assert.That(result.IsSuccess, Is.EqualTo(success));
        _tokens.VerifyAll();
    }

    [Test]
    public async Task FailedCleanupIsNotAcknowledgedForEitherSingleOrMulticastDelivery()
    {
        _tokens.Setup(t => t.GetStringsActiveTokensAsync(_user))
            .ReturnsAsync(Result<List<string>, Error>.Success(["unregistered"]));
        _tokens.Setup(t => t.GetActiveTokenBySessionAsync(_user))
            .ReturnsAsync(Result<string?, Error>.Success("unregistered"));
        _tokens.Setup(t => t.RemoveTokensAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(Govor.Domain.Common.Result.Failure(Error.ServerError("Test.Cleanup", "Unavailable")));
        _provider.Setup(p => p.SendMulticastAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<PushMessage>()))
            .ReturnsAsync(new SendPushResult(0, 1, ["unregistered"]));
        _provider.Setup(p => p.SendToTokenAsync("unregistered", It.IsAny<PushMessage>()))
            .ReturnsAsync(new SendPushResult(0, 1, ["unregistered"]));

        Assert.That((await _service.SendToUserAsync(_user, "title", "body", "chat_messages")).IsFailure, Is.True);
        Assert.That((await _service.SendToSessionAsync(_user, "title", "body", "chat_messages")).IsFailure, Is.True);
    }

    [Test]
    public async Task IncompleteProviderResponseDoesNotLoseTheNotification()
    {
        _tokens.Setup(t => t.GetStringsActiveTokensAsync(_user))
            .ReturnsAsync(Result<List<string>, Error>.Success(["token"]));
        _provider.Setup(p => p.SendMulticastAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<PushMessage>()))
            .ReturnsAsync(new SendPushResult(0, 0, []));
        Assert.That((await _service.SendToUserAsync(_user, "title", "body", "chat_messages")).IsFailure, Is.True);
    }

    [Test]
    public async Task NoTokensIsSuccessfulWithoutContactingProvider()
    {
        _tokens.Setup(t => t.GetStringsActiveTokensAsync(_user))
            .ReturnsAsync(Result<List<string>, Error>.Success([]));
        Assert.That((await _service.SendToUserAsync(_user, "title", "body", "chat_messages")).IsSuccess, Is.True);
        _provider.VerifyNoOtherCalls();
    }
}
