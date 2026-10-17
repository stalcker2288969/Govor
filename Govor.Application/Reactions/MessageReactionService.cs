using Govor.Application.Infrastructure.Common;
using Govor.Application.Messages;
using Govor.Application.Groups;
using Govor.Domain;
using Govor.Domain.Common;
using Govor.Domain.Models.Messages;
using Govor.Domain.Models.Reactions;
using Microsoft.EntityFrameworkCore;
using SmartRes;

namespace Govor.Application.Reactions;

public class MessageReactionService(GovorDbContext context, INowDateTimeProvider clock) : IMessageReactionService
{
    public async Task<Result<MessageReactionState, Error>> GetAsync(Guid userId, Guid messageId)
    {
        var message = await context.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId);
        if (message is null) return Result.Failure<MessageReactionState>(Missing());
        if (!await context.HasChatAccessAsync(userId, message.RecipientId, message.RecipientType))
            return Result.Failure<MessageReactionState>(Denied());
        // A repeatable snapshot keeps version, counts and actor reaction consistent.
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            if (!await context.HasChatAccessAsync(userId, message.RecipientId, message.RecipientType))
                return Result.Failure<MessageReactionState>(Denied());
            var state = await StateAsync(messageId, userId);
            await transaction.CommitAsync();
            return state is null ? Result.Failure<MessageReactionState>(Missing()) : Result.Success(state);
        });
    }

    public async Task<Result<MessageReactionState, Error>> SetAsync(Guid userId, Guid messageId, Guid? reactionId)
    {
        if (reactionId == Guid.Empty)
            return Result.Failure<MessageReactionState>(Error.Validation("Reaction.InvalidId", "A reaction ID is required."));
        
        var message = await context.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId);
        
        if (message is null) return Result.Failure<MessageReactionState>(Missing());
        
        if (!await context.HasChatAccessAsync(userId, message.RecipientId, message.RecipientType))
            return Result.Failure<MessageReactionState>(Denied());
        
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            if (message.RecipientType == RecipientType.Group)
                // Same lock order as channel policy changes; serialize reaction decisions with policy writes.
                await context.ChatGroups.Where(g => g.Id == message.RecipientId)
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.ReactionMode, g => g.ReactionMode));
           
            if (await context.Messages.Where(m => m.Id == messageId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReactionsVersion, m => m.ReactionsVersion)) == 0)
                return Result.Failure<MessageReactionState>(Missing());
            
            if (!await context.HasChatAccessAsync(userId, message.RecipientId, message.RecipientType))
                return Result.Failure<MessageReactionState>(Denied());

            ReactionItem? item = null;
            if (reactionId.HasValue)
            {
                item = await context.ReactionItems.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == reactionId && r.IsEnabled && r.Pack.IsEnabled);
                if (item is null)
                    return Result.Failure<MessageReactionState>(Error.NotFound("Reaction.NotAvailable", "Reaction is not available."));
                
                var channel = message.RecipientType == RecipientType.Group
                    ? await context.ChatGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == message.RecipientId && g.IsChannel)
                    : null;
                
                if (channel is not null && (channel.ReactionMode == ChannelReactionMode.None ||
                    (channel.ReactionMode == ChannelReactionMode.Selected && !await context.ChannelAllowedReactions
                        .AnyAsync(r => r.GroupId == channel.Id && r.ReactionId == item.Id))))
                    return Result.Failure<MessageReactionState>(Error.Forbidden("Reaction.ChannelRestricted", "This reaction is not allowed in the channel."));
            }

            var current = await context.MessageReactions.AsNoTracking().FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId);
            var changed = reactionId.HasValue ? current?.ReactionId != reactionId : current is not null;
            if (changed)
            {
                if (item is null)
                    await context.MessageReactions.Where(r => r.MessageId == messageId && r.UserId == userId).ExecuteDeleteAsync();
                else
                {
                    var id = Guid.NewGuid();
                    var now = clock.Now;
                    await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"MessageReactions\" (\"Id\", \"MessageId\", \"UserId\", \"ReactionId\", \"ReactionCode\", \"ReactedAt\") VALUES ({id}, {messageId}, {userId}, {item.Id}, {item.Code}, {now}) ON CONFLICT (\"MessageId\", \"UserId\") DO UPDATE SET \"ReactionId\" = excluded.\"ReactionId\", \"ReactionCode\" = excluded.\"ReactionCode\", \"ReactedAt\" = excluded.\"ReactedAt\"");
                }
                await context.Messages.Where(m => m.Id == messageId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReactionsVersion, m => m.ReactionsVersion + 1));
            }
            var state = (await StateAsync(messageId, userId))! with { Changed = changed };
            
            await transaction.CommitAsync();
            
            return Result.Success(state);
        });
    }

    private async Task<MessageReactionState?> StateAsync(Guid messageId, Guid actorId)
    {
        var message = await context.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId);
        if (message is null) return null;
        var counts = await context.MessageReactions.AsNoTracking().Where(r => r.MessageId == messageId)
            .GroupBy(r => new { r.ReactionId, r.ReactionCode })
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.ReactionCode)
            .Select(g => new ReactionCount(g.Key.ReactionId, g.Key.ReactionCode, g.Count()))
            .ToListAsync();
        var actor = await context.MessageReactions.AsNoTracking().FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == actorId);
        return new MessageReactionState(messageId, message.RecipientId, message.RecipientType,
            message.ReactionsVersion, actorId, actor, counts);
    }

    public async Task<Result<ChannelReactionPolicy, Error>> GetPolicyAsync(Guid userId, Guid groupId)
    {
        if (!await context.HasChatAccessAsync(userId, groupId, RecipientType.Group))
            return Result.Failure<ChannelReactionPolicy>(Denied());
        
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var policy = await PolicyAsync(groupId);
            await transaction.CommitAsync();
            return Result.Success(policy);
        });
    }

    public async Task<Result<ChannelReactionPolicy, Error>> SetPolicyAsync(Guid userId, Guid groupId,
        ChannelReactionMode mode, IReadOnlyCollection<Guid> reactionIds)
    {
        if (!await context.HasChatAccessAsync(userId, groupId, RecipientType.Group) ||
            !await context.IsGroupAdministratorAsync(groupId, userId))
            return Result.Failure<ChannelReactionPolicy>(Denied());
        if (!Enum.IsDefined(mode) || reactionIds is null || reactionIds.Count > 100 || reactionIds.Contains(Guid.Empty) ||
            (mode != ChannelReactionMode.Selected && reactionIds.Count > 0))
            return Result.Failure<ChannelReactionPolicy>(Error.Validation("Reaction.Policy.Invalid", "Provide a valid mode and at most 100 reaction IDs for Selected mode."));
        var ids = reactionIds.Distinct().ToArray();
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var updated = await context.ChatGroups.Where(g => g.Id == groupId && g.IsChannel)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.ReactionMode, mode)
                    .SetProperty(g => g.ReactionPolicyVersion, g => g.ReactionPolicyVersion + 1));
            if (updated == 0) return Result.Failure<ChannelReactionPolicy>(Error.Validation("Reaction.Policy.NotChannel", "Only channels support reaction restrictions."));
            if (!await context.HasChatAccessAsync(userId, groupId, RecipientType.Group) ||
                !await context.IsGroupAdministratorAsync(groupId, userId))
                return Result.Failure<ChannelReactionPolicy>(Denied());
            if (await context.ReactionItems.CountAsync(r => ids.Contains(r.Id) && r.IsEnabled && r.Pack.IsEnabled) != ids.Length)
                return Result.Failure<ChannelReactionPolicy>(Error.Validation("Reaction.Policy.InvalidSelection", "All reactions must be active and available."));
            await context.ChannelAllowedReactions.Where(r => r.GroupId == groupId).ExecuteDeleteAsync();
            foreach (var id in ids)
                await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"ChannelAllowedReactions\" (\"GroupId\", \"ReactionId\") VALUES ({groupId}, {id})");
            var policy = await PolicyAsync(groupId);
            await transaction.CommitAsync();
            return Result.Success(policy);
        });
    }

    private async Task<ChannelReactionPolicy> PolicyAsync(Guid groupId)
    {
        var group = await context.ChatGroups.AsNoTracking().FirstAsync(g => g.Id == groupId);
        var ids = await context.ChannelAllowedReactions.Where(r => r.GroupId == groupId)
            .OrderBy(r => r.ReactionId).Select(r => r.ReactionId).ToListAsync();
        return new ChannelReactionPolicy(groupId, group.ReactionMode, group.ReactionPolicyVersion, ids);
    }

    private static Error Missing() => Error.NotFound("Message.NotFound", "Message not found.");
    private static Error Denied() => Error.Forbidden("Reaction.AccessDenied", "You do not have access to this chat or operation.");
}
