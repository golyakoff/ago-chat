using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.StartConversation;

/// <summary>
/// `23-78`: <see cref="siteConfig"/> joins this handler's dependencies to read the tenant-level default
/// (<c>WidgetConfig.AllowAttachmentUploadsByDefault</c>) - composes through
/// <see cref="GetSiteConfigByIdHandler"/> rather than a second <c>ISiteRepository.GetByIdAsync</c>
/// read, the same "this handler already owns this read's cache-aside shape" reasoning
/// <c>SendOfflineAutoReplyHandler</c>'s own remarks give for the identical composition. A cache entry
/// up to five minutes stale (<see cref="GetSiteConfigByIdHandler"/>'s own `PositiveOptions`) costs
/// nothing a fresh read would not itself already risk: this value only ever decides what a *brand-new*
/// conversation starts with (`Conversation.Start`'s own remarks - it is never a live gate re-checked
/// later), the identical `adr/0031` "a stamp, not a gate" carve-out `SiteConfigDto`'s own remarks
/// already invoke for `WidgetAutoOpenEnabled` on this same cached read.
/// </summary>
public sealed class StartConversationHandler(
    IVisitorRepository visitors,
    IConversationRepository conversations,
    GetSiteConfigByIdHandler siteConfig,
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
            return new StartConversationResult(existing.Id, IsNew: false, existing.HasAttachmentUploadGrant);
        }

        var config = await siteConfig.HandleAsync(new GetSiteConfigById.GetSiteConfigById(command.SiteId), cancellationToken);
        var attachmentUploadGrantedByDefault = config?.WidgetAllowAttachmentUploadsByDefault ?? false;

        var conversationId = new ConversationId(idGenerator.NewId(now));
        var conversation = Conversation.Start(
            conversationId, command.SiteId, command.VisitorId, now, command.Source, attachmentUploadGrantedByDefault);
        await conversations.SaveAsync(conversation, cancellationToken);

        return new StartConversationResult(conversation.Id, IsNew: true, conversation.HasAttachmentUploadGrant);
    }
}
