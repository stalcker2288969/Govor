using Govor.Application.Infrastructure.Common;
using Govor.Contracts.Responses.SignalR;
using Govor.Domain;
using Microsoft.EntityFrameworkCore;

namespace Govor.API.Hubs.Infrastructure;

public class ChatPushDispatcher(GovorDbContext context, IChatNotificationService notifier,
    INowDateTimeProvider dateTimeProvider, ILogger<ChatPushDispatcher> logger = null)
{
    public async Task DispatchAsync(CancellationToken cancellationToken = default)
    {
        var now = dateTimeProvider.Now;
        var pending = await context.ChatPushNotifications.AsNoTracking()
            .Where(n => n.NextAttemptAt <= now).OrderBy(n => n.NextAttemptAt)
            .Take(20).Select(n => new { n.MessageId, n.Attempts }).ToListAsync(cancellationToken);

        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = item.MessageId;
            now = dateTimeProvider.Now;
            // Conditional update claims a two-minute lease, including across server instances.
            // An interrupted worker leaves a retryable row rather than losing the notification.
            var claimed = await context.ChatPushNotifications
                .Where(n => n.MessageId == id && n.NextAttemptAt <= now && n.Attempts == item.Attempts)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.NextAttemptAt, now.AddMinutes(2))
                    .SetProperty(n => n.Attempts, n => n.Attempts + 1), cancellationToken);
            if (claimed == 0)
                continue;

            var attempt = item.Attempts + 1;
            // An old worker must not complete or reschedule a newer worker's lease.
            var owned = context.ChatPushNotifications.Where(n => n.MessageId == id && n.Attempts == attempt);
            var delivered = false;
            try
            {
                var message = await context.Messages.AsNoTracking()
                    .Where(m => m.Id == id).Select(m => new UserMessageResponse
                    {
                        MessageId = m.Id, SenderId = m.SenderId, RecipientId = m.RecipientId,
                        RecipientType = m.RecipientType, EncryptedContent = m.EncryptedContent,
                        SentAt = m.SentAt, ReplyToMessageId = m.ReplyToMessageId
                    }).FirstOrDefaultAsync(cancellationToken);
                delivered = message is null || await notifier.DeliverPushAsync(message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Push attempt failed for message {MessageId}; delivery will be retried.", id);
            }
            if (delivered)
                await owned.ExecuteDeleteAsync(cancellationToken);
            else
            {
                var retryAt = dateTimeProvider.Now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempt, 9))));
                await owned
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.NextAttemptAt, retryAt), cancellationToken);
            }
        }
    }
}
