using Govor.Application.Interfaces.PushNotifications;
using Govor.Application.Interfaces.PushNotifications.Models;

namespace Govor.Application.PushNotifications.Providers;

public class NullPushProvider : IPushNotificationProvider
{
    public string Name => "NULL";
    
    public Task<SendPushResult> SendToTokenAsync(string token, PushMessage message)
    {
        return Task.FromResult(new SendPushResult(1, 0, []));
    }

    public Task<SendPushResult> SendMulticastAsync(IReadOnlyList<string> tokens, PushMessage message)
    {
        return Task.FromResult(new SendPushResult(tokens.Count, 0, []));
    }
}
