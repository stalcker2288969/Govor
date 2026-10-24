using Govor.Application.PushNotifications;
using Govor.Application.PushNotifications.Providers;
using Govor.Application.Interfaces.PushNotifications.Models;
using Govor.Domain.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Govor.Application.Tests.Unit.PushNotifications;

[TestFixture]
public class PushNotificationServiceTests
{
    private Mock<IPushNotificationProvider> _provider = null!;
    private Mock<IPushTokenService> _tokens = null!;
    private PushNotificationService _service = null!;
    private readonly Guid _recipient = Guid.NewGuid();
    private static readonly Error Failure = Error.ServerError("Test.Failure", "unavailable");

    [SetUp]
    public void SetUp()
    {
        _provider = new Mock<IPushNotificationProvider>(MockBehavior.Strict);
        _tokens = new Mock<IPushTokenService>(MockBehavior.Strict);
        _service = new(_provider.Object, _tokens.Object, NullLogger<PushNotificationService>.Instance);
    }

    [Test]
    public async Task TokenLookupErrorsAreReturnedWithoutSending()
    {
        _tokens.Setup(t => t.GetStringsActiveTokensAsync(_recipient)).ReturnsAsync(Result.Failure<List<string>>(Failure));
        _tokens.Setup(t => t.GetUsersStringsActiveTokensAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(Result.Failure<List<string>>(Failure));
        _tokens.Setup(t => t.GetActiveTokenBySessionAsync(_recipient)).ReturnsAsync(Result.Failure<string?>(Failure));
        Assert.That((await _service.SendToUserAsync(_recipient, "t", "b", "c")).Error, Is.SameAs(Failure));
        Assert.That((await _service.SendToUsersAsync(new[] { _recipient }, "t", "b", "c")).Error, Is.SameAs(Failure));
        Assert.That((await _service.SendToSessionAsync(_recipient, "t", "b", "c")).Error, Is.SameAs(Failure));
        _provider.VerifyNoOtherCalls();
    }

    [Test]
    public async Task MissingTokensAreSuccessfulNoOpsForEveryDeliveryMode()
    {
        _tokens.Setup(t => t.GetStringsActiveTokensAsync(_recipient)).ReturnsAsync(new List<string>());
        _tokens.Setup(t => t.GetUsersStringsActiveTokensAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<string>());
        _tokens.Setup(t => t.GetActiveTokenBySessionAsync(_recipient)).ReturnsAsync((string?)null);
        Assert.That((await _service.SendToUserAsync(_recipient, "t", "b", "c")).IsSuccess, Is.True);
        Assert.That((await _service.SendToUsersAsync(new[] { _recipient }, "t", "b", "c")).IsSuccess, Is.True);
        Assert.That((await _service.SendToSessionAsync(_recipient, "t", "b", "c")).IsSuccess, Is.True);
        _provider.VerifyNoOtherCalls();
    }

    [Test]
    public async Task EmptyOrNullBulkRecipientsAreRejectedBeforeTokenLookup()
    {
        Assert.That((await _service.SendToUsersAsync(Array.Empty<Guid>(), "t", "b", "c")).Error.Code, Is.EqualTo("Push.InvalidArgs"));
        Assert.That((await _service.SendToUsersAsync(null!, "t", "b", "c")).IsFailure, Is.True);
        _tokens.VerifyNoOtherCalls();
        _provider.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SessionDeliveryPreservesPayload()
    {
        var data = new Dictionary<string, string> { ["id"] = "message" };
        _tokens.Setup(t => t.GetActiveTokenBySessionAsync(_recipient)).ReturnsAsync("device");
        _provider.Setup(p => p.SendToTokenAsync("device", It.Is<PushMessage>(m => m.Title == "title" &&
            m.Body == "body" && m.ChannelId == "channel" && m.Tag == "tag" && m.Data == data)))
            .ReturnsAsync(new SendPushResult(1, 0, Array.Empty<string>()));
        Assert.That((await _service.SendToSessionAsync(_recipient, "title", "body", "channel", "tag", data)).IsSuccess, Is.True);
        _provider.VerifyAll();
    }

    [Test]
    public async Task SessionProviderExceptionBecomesFailureAndKeepsToken()
    {
        _tokens.Setup(t => t.GetActiveTokenBySessionAsync(_recipient)).ReturnsAsync("device");
        _provider.Setup(p => p.SendToTokenAsync("device", It.IsAny<PushMessage>())).ThrowsAsync(new IOException("provider offline"));
        Assert.That((await _service.SendToSessionAsync(_recipient, "t", "b", "c")).IsFailure, Is.True);
        _tokens.Verify(t => t.RemoveTokensAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Test]
    public async Task BulkRecipientsAndTokensAreDeduplicatedAndBatchingContinuesAfterFailure()
    {
        var tokens = Enumerable.Range(0, 501).Select(i => "device-" + i).ToList();
        tokens.Add(tokens[0]);
        _tokens.Setup(t => t.GetUsersStringsActiveTokensAsync(It.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { _recipient }))))
            .ReturnsAsync(tokens);
        var batches = new List<int>();
        _provider.Setup(p => p.SendMulticastAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<PushMessage>()))
            .Returns((IReadOnlyList<string> batch, PushMessage message) =>
            {
                batches.Add(batch.Count);
                Assert.That(message.Data, Is.Empty);
                return batches.Count == 1 ? Task.FromException<SendPushResult>(new IOException("offline")) :
                    Task.FromResult(new SendPushResult(batch.Count, 0, Array.Empty<string>()));
            });
        var result = await _service.SendToUsersAsync(new[] { _recipient, _recipient }, "t", "b", "c");
        Assert.That(result.Error.Code, Is.EqualTo("Push.DeliveryFailed"));
        Assert.That(batches, Is.EqualTo(new[] { 500, 1 }));
        _tokens.Verify(t => t.RemoveTokensAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Test]
    public async Task OnlyUnregisteredTokensFromThisBatchCanBeRemoved()
    {
        _tokens.Setup(t => t.GetStringsActiveTokensAsync(_recipient)).ReturnsAsync(new List<string> { "valid", "gone" });
        _provider.Setup(p => p.SendMulticastAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<PushMessage>()))
            .ReturnsAsync(new SendPushResult(1, 1, new[] { "gone", "gone", "unrelated" }));
        _tokens.Setup(t => t.RemoveTokensAsync(It.Is<IEnumerable<string>>(ts => ts.SequenceEqual(new[] { "gone" }))))
            .ReturnsAsync(Result.Success());
        Assert.That((await _service.SendToUserAsync(_recipient, "t", "b", "c")).IsSuccess, Is.True);
        _tokens.VerifyAll();
    }

    [Test]
    public async Task TokenRemovalFailureIsPropagated()
    {
        _tokens.Setup(t => t.GetActiveTokenBySessionAsync(_recipient)).ReturnsAsync("gone");
        _provider.Setup(p => p.SendToTokenAsync("gone", It.IsAny<PushMessage>())).ReturnsAsync(new SendPushResult(0, 1, new[] { "gone" }));
        _tokens.Setup(t => t.RemoveTokensAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(Result.Failure(Failure));
        Assert.That((await _service.SendToSessionAsync(_recipient, "t", "b", "c")).Error, Is.SameAs(Failure));
    }

    [TestCase(0, 0)]
    [TestCase(0, 1)]
    [TestCase(2, 0)]
    public async Task IncompleteOrInconsistentProviderCountsAreFailures(int success, int failed)
    {
        _tokens.Setup(t => t.GetActiveTokenBySessionAsync(_recipient)).ReturnsAsync("device");
        _provider.Setup(p => p.SendToTokenAsync("device", It.IsAny<PushMessage>()))
            .ReturnsAsync(new SendPushResult(success, failed, Array.Empty<string>()));
        Assert.That((await _service.SendToSessionAsync(_recipient, "t", "b", "c")).Error.Code, Is.EqualTo("Push.DeliveryFailed"));
    }

    [Test]
    public async Task NullProviderAcceptsSingleAndMulticastWithoutNetwork()
    {
        var provider = new NullPushProvider();
        Assert.That(provider.Name, Is.EqualTo("NULL"));
        Assert.That((await provider.SendToTokenAsync("device", new PushMessage())).SuccessCount, Is.EqualTo(1));
        var result = await provider.SendMulticastAsync(new[] { "a", "b" }, new PushMessage());
        Assert.That(result.SuccessCount, Is.EqualTo(2));
        Assert.That(result.FailureCount, Is.Zero);
        Assert.That(result.FailedTokens, Is.Empty);
    }
}

