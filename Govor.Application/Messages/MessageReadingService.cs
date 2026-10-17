using Govor.Application.Infrastructure.Common;
using ChatReadResult = Govor.Application.Messages.Parameters.ChatReadResult;
using Govor.Domain;
using Govor.Domain.Common;
using Govor.Domain.Models.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartRes;

namespace Govor.Application.Messages;

public class MessageReadingService : IMessageReadingService
{
    private readonly GovorDbContext _dbContext;
    private readonly ILogger<MessageReadingService> _logger;
    private readonly INowDateTimeProvider _dateTimeProvider;

    public MessageReadingService(
        GovorDbContext dbContext,
        ILogger<MessageReadingService> logger,
        INowDateTimeProvider dateTimeProvider)
    {
        _dbContext = dbContext;
        _logger = logger;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result<Message, Error>> ReadMessageAsync(Guid readerId, Guid messageId)
    {
        var message = await _dbContext.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId);
        if (message is null)
            return Result.Failure<Message>(Error.NotFound("Message.NotFound", "Message not found."));

        var result = await ReadChatAsync(readerId, message.RecipientId,
            message.RecipientType, new[] { messageId });
        if (result.IsFailure)
            return Result.Failure<Message>(result.Error);

        // Fresh query also returns the existing receipt on repeated calls.
        var readMessage = await _dbContext.Messages.AsNoTracking().Include(m => m.MessageViews)
            .FirstOrDefaultAsync(m => m.Id == messageId);
        return readMessage is null
            ? Result.Failure<Message>(Error.NotFound("Message.NotFound", "Message was removed."))
            : Result<Message, Error>.Success(readMessage);
    }

    public async Task<Result<ChatReadResult, Error>> ReadChatAsync(Guid readerId, Guid chatId,
        RecipientType type, IReadOnlyCollection<Guid>? messageIds = null, Guid? upToMessageId = null)
    {
        if (!await _dbContext.HasChatAccessAsync(readerId, chatId, type))
            return Result.Failure<ChatReadResult>(Denied());

        if (messageIds is not null && (messageIds.Count == 0 || messageIds.Count > 100 ||
            messageIds.Contains(Guid.Empty) || upToMessageId.HasValue))
            return Result.Failure<ChatReadResult>(Error.Validation("Message.Read.InvalidSelection",
                "Provide 1 to 100 message IDs or an inclusive message boundary, not both."));

        var chat = _dbContext.Messages.AsNoTracking()
            .Where(m => m.RecipientId == chatId && m.RecipientType == type);
        Guid[]? ids = messageIds?.Distinct().ToArray();
        Message? boundary = null;
        if (ids is not null)
        {
            // Validate the whole selection before writing any receipt.
            if (await chat.CountAsync(m => ids.Contains(m.Id)) != ids.Length)
                return Result.Failure<ChatReadResult>(Error.NotFound("Message.Read.InvalidSelection",
                    "One or more messages do not belong to this chat."));
            if (await chat.AnyAsync(m => ids.Contains(m.Id) && m.SenderId == readerId))
                return Result.Failure<ChatReadResult>(Error.Validation("Message.Read.OwnMessage",
                    "Only incoming messages can be marked as read."));
        }
        else
        {
            boundary = upToMessageId.HasValue
                ? await chat.FirstOrDefaultAsync(m => m.Id == upToMessageId.Value)
                : await chat.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id)
                    .FirstOrDefaultAsync();
            if (upToMessageId.HasValue && boundary is null)
                return Result.Failure<ChatReadResult>(Error.NotFound("Message.Read.BoundaryNotFound",
                    "The boundary message does not belong to this chat."));
        }

        var readAt = _dateTimeProvider.Now;
        var readCount = 0;
        var selected = chat.Where(m => m.SenderId != readerId);
        if (ids is not null)
            selected = selected.Where(m => ids.Contains(m.Id));
        else if (boundary is not null)
        {
            var sentAt = boundary.SentAt;
            var id = boundary.Id;
            selected = selected.Where(m => m.SentAt < sentAt ||
                (m.SentAt == sentAt && m.Id.CompareTo(id) <= 0));
        }
        else
            selected = selected.Where(m => false);

        // Small bounded batches; PostgreSQL/SQLite ON CONFLICT handles concurrent devices
        // against the existing unique (MessageId, UserId) index without losing receipts.
        while (true)
        {
            var pending = await selected.Where(m => !_dbContext.MessageViews
                    .Any(v => v.MessageId == m.Id && v.UserId == readerId))
                .Select(m => m.Id).Take(200).ToListAsync();
            if (pending.Count == 0)
                break;

            var values = new List<string>();
            var parameters = new List<object>();
            using var command = _dbContext.Database.GetDbConnection().CreateCommand();
            foreach (var id in pending)
            {
                var names = new List<string>();
                foreach (var value in new object[] { Guid.NewGuid(), id, readerId, readAt })
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "p" + parameters.Count;
                    parameter.Value = value;
                    parameters.Add(parameter);
                    names.Add("@" + parameter.ParameterName);
                }
                values.Add("SELECT " + string.Join(", ", names) +
                    " WHERE EXISTS (SELECT 1 FROM \"Messages\" WHERE \"Id\" = " + names[1] + ")");
            }
            var sql = "INSERT INTO \"MessageViews\" (\"Id\", \"MessageId\", \"UserId\", \"ViewedAt\") " +
                string.Join(" UNION ALL ", values) + " ON CONFLICT (\"MessageId\", \"UserId\") DO NOTHING";
            readCount += await _dbContext.Database.ExecuteSqlRawAsync(sql, parameters);
        }

        var unreadCount = await CountUnreadAsync(readerId, chat);
        _logger.LogInformation("User {ReaderId} marked {Count} messages read in chat {ChatId}",
            readerId, readCount, chatId);
        return new ChatReadResult(chatId, type, readerId, readAt,
            boundary?.Id, boundary?.SentAt, ids, readCount, unreadCount);
    }

    public async Task<Result<int, Error>> GetUnreadCountAsync(Guid readerId, Guid chatId, RecipientType type)
    {
        if (!await _dbContext.HasChatAccessAsync(readerId, chatId, type))
            return Result.Failure<int>(Denied());

        return await CountUnreadAsync(readerId, _dbContext.Messages.AsNoTracking()
            .Where(m => m.RecipientId == chatId && m.RecipientType == type));
    }

    private Task<int> CountUnreadAsync(Guid readerId, IQueryable<Message> chat) =>
        chat.CountAsync(m => m.SenderId != readerId &&
            !_dbContext.MessageViews.Any(v => v.MessageId == m.Id && v.UserId == readerId));

    private static Error Denied() => Error.Forbidden("Chat.AccessDenied",
        "You are not a member of this chat.");
}
