using FirebaseAdmin.Messaging;
using Govor.Application.Interfaces.PushNotifications.Models;
using Microsoft.Extensions.Logging;

namespace Govor.Application.PushNotifications.Providers;

public class FirebasePushProvider : IPushNotificationProvider
{
    private readonly ILogger<FirebasePushProvider> _logger;
    private readonly FirebaseMessaging? _messaging;
    public FirebasePushProvider(ILogger<FirebasePushProvider> logger, FirebaseMessaging? messaging = null)
    {
        _logger = logger;
        _messaging = messaging;
    }
    
    public string Name => "FCM";

    public async Task<SendPushResult> SendToTokenAsync(string token, PushMessage message)
    {
        var msg = BuildFcmMessage(message, token: token);

        try
        {
            await (_messaging ?? FirebaseMessaging.DefaultInstance).SendAsync(msg);
            return new SendPushResult(1, 0, []);
        }
        catch (FirebaseMessagingException ex)
        {
            if (IsInvalidTokenError(ex))
            {
                _logger.LogInformation("FCM registration is no longer valid");
                return new SendPushResult(0, 1,  [token]);
            }

            throw; // return failed
        }
    }

    public async Task<SendPushResult> SendMulticastAsync(IReadOnlyList<string> tokens, PushMessage message)
    {
        tokens = tokens.Distinct().ToArray();
        if (tokens.Count == 0)
            return new SendPushResult(0, 0, []);

        if (tokens.Count > 500)
        {
            var success = 0;
            var failure = 0;
            var invalid = new List<string>();
            foreach (var batch in tokens.Distinct().Chunk(500))
            {
                var result = await SendMulticastAsync(batch, message);
                success += result.SuccessCount;
                failure += result.FailureCount;
                invalid.AddRange(result.FailedTokens);
            }
            return new SendPushResult(success, failure, invalid);
        }
        
        var multicastMsg = new MulticastMessage
        {
            Tokens = tokens,
            Notification = new Notification
            {
                Title = message.Title,
                Body  = message.Body,
            },
            Data = message.Data,
            Android = BuildAndroidConfig(message),
            Apns = BuildApnsConfig(message),
            Webpush = BuildWebpushConfig(message)
        };

        try
        {
            var response = await (_messaging ?? FirebaseMessaging.DefaultInstance).SendEachForMulticastAsync(multicastMsg);

            var failedTokens = new List<string>();

            for (int i = 0; i < response.Responses.Count; i++)
            {
                if (!response.Responses[i].IsSuccess)
                {
                    var ex = response.Responses[i].Exception;

                    if (ex != null && IsInvalidTokenError(ex))
                    {
                        failedTokens.Add(tokens[i]); // invalid
                    }
                }
            }

            return new SendPushResult(
                response.SuccessCount,
                response.FailureCount,
                failedTokens
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FCM multicast failed");
            throw; // A provider outage does not invalidate device tokens.
        }
    }

    private Message BuildFcmMessage(PushMessage pm, string? token = null)
    {
        var msg = new Message
        {
            Notification = new Notification
                { 
                    Title = pm.Title,
                    Body = pm.Body
                },
            Data = pm.Data,
            Token = token,
            Android = BuildAndroidConfig(pm),
            Apns = BuildApnsConfig(pm),
            Webpush = BuildWebpushConfig(pm)
        };

        return msg;
    }

    private static AndroidConfig BuildAndroidConfig(PushMessage message) => new()
    {
        Priority = Priority.High,
        Notification = new AndroidNotification
        {
            ChannelId = string.IsNullOrWhiteSpace(message.ChannelId) ? "chat_messages" : message.ChannelId,
            Tag = message.Tag,
            Sound = "default"
        }
    };

    private static ApnsConfig BuildApnsConfig(PushMessage message) => new()
    {
        Headers = new Dictionary<string, string> { ["apns-push-type"] = "alert", ["apns-priority"] = "10" },
        Aps = new Aps { Sound = "default", ThreadId = string.IsNullOrEmpty(message.Tag) ? null : message.Tag }
    };

    private static WebpushConfig BuildWebpushConfig(PushMessage message) => new()
    {
        Notification = new WebpushNotification { Tag = message.Tag }
    };

    private static bool IsInvalidTokenError(FirebaseMessagingException ex)
    {
        return ex.MessagingErrorCode is MessagingErrorCode.Unregistered;
    }
}
