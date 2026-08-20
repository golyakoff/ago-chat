using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.StartConversation;

public sealed class StartConversationHandler(
    IVisitorRepository visitors,
    IConversationRepository conversations,
    IClock clock,
    IIdGenerator idGenerator)
{
    public async Task<Result<StartConversationResult>> HandleAsync(
        StartConversation command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var visitor = await visitors.GetByIdAsync(command.VisitorId, cancellationToken);
        if (visitor is null)
        {
            visitor = new Visitor(command.VisitorId, command.SiteId, now);
        }
        else
        {
            visitor.Touch(now);
        }

        await visitors.SaveAsync(visitor, cancellationToken);

        var existing = await conversations.GetActiveForVisitorAsync(command.VisitorId, cancellationToken);
        if (existing is not null)
        {
            return new StartConversationResult(existing.Id, IsNew: false);
        }

        var conversationId = new ConversationId(idGenerator.NewId(now));
        var conversation = Conversation.Start(conversationId, command.SiteId, command.VisitorId, now);
        await conversations.SaveAsync(conversation, cancellationToken);

        return new StartConversationResult(conversation.Id, IsNew: true);
    }
}
