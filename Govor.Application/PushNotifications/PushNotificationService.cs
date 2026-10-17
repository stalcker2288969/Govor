using Govor.Application.Interfaces.PushNotifications.Models;
using Govor.Application.PushNotifications.Providers;
using Govor.Domain.Common;
using Microsoft.Extensions.Logging;
using SmartRes;

namespace Govor.Application.PushNotifications;

public class PushNotificationService : IPushNotificationService
{
    private readonly IPushNotificationProvider _provider;
    private readonly IPushTokenService _tokenService; 
    private readonly ILogger<PushNotificationService> _logger;

    public PushNotificationService(
        IPushNotificationProvider provider,
        IPushTokenService tokenService,
        ILogger<PushNotificationService> logger)
    {
        _provider = provider;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task<Result<Unit, Error>>  SendToUserAsync(Guid userId, string title, string body, string channelId, string tag = "", Dictionary<string, string>? data = null)
    {
        var resultTokens = await _tokenService.GetStringsActiveTokensAsync(userId);
        if (resultTokens.IsFailure)
            return Result.Failure(resultTokens.Error);

        var tokens = resultTokens.Value;
        if (tokens.Count == 0)
        {
            _logger.LogInformation("No active push tokens for user {UserId}", userId);
            return Result.Success();
        }

        return await SendMulticastInternalAsync(tokens, title, body, channelId, tag, data, userId.ToString());
    }

    public async Task<Result<Unit, Error>>  SendToUsersAsync(IEnumerable<Guid> userIds, string title, string body, string channelId, string tag = "", Dictionary<string, string>? data = null)
    {
        var recipients = userIds?.Distinct().ToArray();
        if (recipients is null || recipients.Length == 0)
            return Result.Failure(Error.Failure("Push.InvalidArgs", "User IDs collection cannot be empty."));

        var tokensResult = await _tokenService.GetUsersStringsActiveTokensAsync(recipients);
        if (tokensResult.IsFailure)
            return Result.Failure(tokensResult.Error);
        
        var tokens = tokensResult.Value;
        if (tokens.Count == 0)
            return Result.Success();

        return await SendMulticastInternalAsync(tokens, title, body, channelId, tag, data, "bulk_request");
    }

    public async Task<Result<Unit, Error>>  SendToSessionAsync(Guid sessionId, string title, string body, string channelId, string tag = "", Dictionary<string, string>? data = null)
    {
        var tokenResult = await _tokenService.GetActiveTokenBySessionAsync(sessionId);
        if (tokenResult.IsFailure)
            return Result.Failure(tokenResult.Error);

        var token = tokenResult.Value;
        if (string.IsNullOrEmpty(token))
            return Result.Success();

        try
        {
            var message = CreateMessage(title, body, channelId, tag, data);
            var result = await _provider.SendToTokenAsync(token, message);
            
            return await HandleResultAsync(result, [token]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send single push notification to session {SessionId}", sessionId);
            return Result.Failure(ex);
        }
    }
    
    private async Task<Result<Unit, Error>> SendMulticastInternalAsync(List<string> tokens, string title, string body, string channelId, string tag, Dictionary<string, string>? data, string targetInfo)
    {
        Error? failure = null;
        var message = CreateMessage(title, body, channelId, tag, data);
        // FCM accepts at most 500 tokens per call. Process batches independently so a
        // failed batch does not prevent unrelated devices from receiving a message.
        foreach (var batch in tokens.Distinct().Chunk(500))
        {
            try
            {
                var result = await _provider.SendMulticastAsync(batch, message);
                var handled = await HandleResultAsync(result, batch);
                if (handled.IsFailure)
                    failure ??= handled.Error;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Push batch failed for target {Target}", targetInfo);
                failure ??= Error.ServerError("Push.DeliveryFailed", "Push delivery failed temporarily.");
            }
        }

        return failure is null ? Result.Success() : Result.Failure(failure);
    }

    private async Task<Result<Unit, Error>> HandleResultAsync(SendPushResult result, IReadOnlyList<string> tokens)
    {
        // Only a permanent registration failure authorizes deleting a token. Provider
        // outages, malformed payloads and authentication errors must leave tokens intact.
        var invalid = result.FailedTokens.Distinct().Intersect(tokens).ToArray();
        if (invalid.Length > 0)
        {
            var removed = await _tokenService.RemoveTokensAsync(invalid);
            if (removed.IsFailure)
                return Result.Failure(removed.Error);
            _logger.LogInformation("Removed {Count} unregistered push tokens", invalid.Length);
        }

        if (result.SuccessCount + result.FailureCount != tokens.Count || result.FailureCount > invalid.Length)
        {
            _logger.LogWarning("Push delivery incomplete: {SuccessCount} accepted, {FailureCount} failed, {InvalidCount} unregistered",
                result.SuccessCount, result.FailureCount, invalid.Length);
            return Result.Failure(Error.ServerError("Push.DeliveryFailed", "Push delivery failed temporarily."));
        }
        return Result.Success();
    }

    private static PushMessage CreateMessage(string title, string body, string channelId, string tag, Dictionary<string, string>? data)
    {
        return new PushMessage
        {
            Title = title,
            Body = body,
            Data = data ?? new(),
            Tag = tag,
            ChannelId = channelId
        };
    }
}
