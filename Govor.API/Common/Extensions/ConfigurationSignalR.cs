using Govor.API.Filters;
using Microsoft.AspNetCore.SignalR;

namespace Govor.API.Common.Extensions;

public static class ConfigurationSignalR
{
    public static void AddSignalRConf(this IServiceCollection services)
    {
        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 1_100_000;
            options.AddFilter<HubExceptionFilter>();
        });
    }
}
