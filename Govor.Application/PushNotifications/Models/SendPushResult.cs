namespace Govor.Application.Interfaces.PushNotifications.Models;

/// <param name="SuccessCount">Tokens accepted by the provider.</param>
/// <param name="FailureCount">All failed sends, including retryable errors.</param>
/// <param name="FailedTokens">Only permanently unregistered tokens that can safely be deleted.</param>
public record SendPushResult(int SuccessCount, int FailureCount, IEnumerable<string> FailedTokens);
