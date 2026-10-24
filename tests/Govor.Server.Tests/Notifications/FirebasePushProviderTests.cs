using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Govor.Application.Interfaces.PushNotifications.Models;
using Govor.Application.PushNotifications.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Govor.Server.Tests;

[TestFixture]
public class FirebasePushProviderTests
{
    private FirebaseApp _app = null!;
    private FirebasePushProvider _provider = null!;
    private readonly ConcurrentQueue<JsonElement> _requests = new();
    private HttpStatusCode _status;
    private string _response = "";

    [SetUp]
    public void SetUp()
    {
        _requests.Clear();
        _status = HttpStatusCode.OK;
        _response = """{"name":"projects/test/messages/accepted"}""";
        _app = FirebaseApp.Create(new AppOptions
        {
            ProjectId = "test",
            Credential = GoogleCredential.FromAccessToken("test-access-token"),
            HttpClientFactory = new StubHttpFactory(async request =>
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                _requests.Enqueue(json.RootElement.GetProperty("message").Clone());
                return new HttpResponseMessage(_status) { Content = new StringContent(_response, Encoding.UTF8, "application/json") };
            })
        }, Guid.NewGuid().ToString());
        _provider = new FirebasePushProvider(NullLogger<FirebasePushProvider>.Instance, FirebaseMessaging.GetMessaging(_app));
    }

    [TearDown]
    public void TearDown() => _app.Delete();

    [TestCase(false)]
    [TestCase(true)]
    public async Task SingleAndMulticastUseEquivalentPlatformPayloads(bool multicast)
    {
        var message = new PushMessage { Title = "Alice", Body = "New message", ChannelId = "chat_messages",
            Tag = "chat_123", Data = new() { ["messageId"] = "123" } };
        var result = multicast
            ? await _provider.SendMulticastAsync(new[] { "device", "device" }, message)
            : await _provider.SendToTokenAsync("device", message);
        Assert.That(result.SuccessCount, Is.EqualTo(1));
        var sent = _requests.Single();
        Assert.That(sent.GetProperty("token").GetString(), Is.EqualTo("device"));
        Assert.That(sent.GetProperty("data").GetProperty("messageId").GetString(), Is.EqualTo("123"));
        Assert.That(sent.GetProperty("android").GetProperty("priority").GetString(), Is.EqualTo("high"));
        var android = sent.GetProperty("android").GetProperty("notification");
        Assert.That(android.GetProperty("channel_id").GetString(), Is.EqualTo("chat_messages"));
        Assert.That(android.GetProperty("tag").GetString(), Is.EqualTo("chat_123"));
        var apns = sent.GetProperty("apns");
        Assert.That(apns.GetProperty("headers").GetProperty("apns-push-type").GetString(), Is.EqualTo("alert"));
        Assert.That(apns.GetProperty("payload").GetProperty("aps").GetProperty("thread-id").GetString(), Is.EqualTo("chat_123"));
        Assert.That(sent.GetProperty("webpush").GetProperty("notification").GetProperty("tag").GetString(), Is.EqualTo("chat_123"));
    }

    [Test]
    public async Task LargeMulticastBatchesDistinctTokensAndUsesDefaultPlatformChannel()
    {
        var tokens = Enumerable.Range(0, 501).Select(i => "device-" + i).ToArray();
        var result = await _provider.SendMulticastAsync(tokens.Append(tokens[0]).ToArray(), new PushMessage { Title = "test", Body = "test" });
        Assert.That(result.SuccessCount, Is.EqualTo(501));
        Assert.That(result.FailureCount, Is.Zero);
        Assert.That(_requests.Select(r => r.GetProperty("token").GetString()), Is.EquivalentTo(tokens));
        Assert.That(_requests.First().GetProperty("android").GetProperty("notification").GetProperty("channel_id").GetString(), Is.EqualTo("chat_messages"));
        Assert.That((await _provider.SendMulticastAsync(Array.Empty<string>(), new PushMessage())).SuccessCount, Is.Zero);
    }

    [TestCase("UNREGISTERED", HttpStatusCode.NotFound, true)]
    [TestCase("INVALID_ARGUMENT", HttpStatusCode.BadRequest, false)]
    public async Task SingleDeliveryInvalidatesOnlyPermanentlyUnregisteredDevice(string code, HttpStatusCode status, bool permanent)
    {
        _status = status;
        _response = JsonSerializer.Serialize(new { error = new { code = (int)status, message = "FCM failure",
            status = status == HttpStatusCode.NotFound ? "NOT_FOUND" : "INVALID_ARGUMENT",
            details = new[] { new Dictionary<string, string> { ["@type"] = "type.googleapis.com/google.firebase.fcm.v1.FcmError", ["errorCode"] = code } } } });
        if (permanent)
        {
            var result = await _provider.SendToTokenAsync("device", new PushMessage { Title = "test", Body = "test" });
            Assert.That(result.SuccessCount, Is.Zero);
            Assert.That(result.FailedTokens, Is.EqualTo(new[] { "device" }));
        }
        else Assert.ThrowsAsync<FirebaseMessagingException>(() => _provider.SendToTokenAsync("device", new PushMessage { Title = "test", Body = "test" }));
    }

    [TestCase("UNREGISTERED", HttpStatusCode.NotFound, true)]
    [TestCase("INVALID_ARGUMENT", HttpStatusCode.BadRequest, false)]
    [TestCase("SENDER_ID_MISMATCH", HttpStatusCode.Forbidden, false)]
    public async Task MulticastInvalidatesOnlyUnregisteredTokens(string code, HttpStatusCode status, bool invalid)
    {
        _status = status;
        _response = JsonSerializer.Serialize(new { error = new { code = (int)status, message = "FCM failure",
            status = status == HttpStatusCode.NotFound ? "NOT_FOUND" : status == HttpStatusCode.BadRequest ? "INVALID_ARGUMENT" : "PERMISSION_DENIED",
            details = new[] { new Dictionary<string, string> { ["@type"] = "type.googleapis.com/google.firebase.fcm.v1.FcmError", ["errorCode"] = code } } } });

        var result = await _provider.SendMulticastAsync(new[] { "device" }, new PushMessage { Title = "test", Body = "test" });

        Assert.That(result.FailureCount, Is.EqualTo(1));
        Assert.That(result.FailedTokens, Is.EqualTo(invalid ? new[] { "device" } : Array.Empty<string>()));
    }

    private sealed class StubHttpFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : Google.Apis.Http.HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new StubHandler(send);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
