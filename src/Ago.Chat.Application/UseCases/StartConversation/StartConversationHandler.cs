using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.StartConversation;

/// <summary>
/// `23-76`: <see cref="rateLimiter"/>/<see cref="rateLimitOptions"/> join this handler's dependencies
/// so that creating conversations at speed cannot multiply `23-75`'s per-conversation attachment
/// budget - a visitor is free to mint a new identity and open a new conversation, and without this
/// check that budget "is a speed bump, not a limit" (this item's own words). See
/// <see cref="ConversationCreateRateLimitOptions"/>'s own remarks for the two buckets and which one
/// actually bounds the threat this item describes.
///
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
///
/// <para><b>Found live 2026-09-12: two concurrent callers for the same visitor both saw
/// <see cref="IConversationRepository.GetActiveForVisitorAsync"/> answer null and both created a
/// conversation.</b> Two browser tabs sharing one persisted <c>visitor_id</c>, or a widget reconnect
/// racing its own prior connection, both reach this method before either has committed. The read at
/// line 50 below cannot see a row that has not been saved yet, no matter how it is written - the fix
/// is not a better read, it is `ConversationConfiguration`'s own
/// <c>ix_conversations_one_open_per_visitor</c>, which turns the loser's <c>SaveAsync</c> into a real
/// Postgres unique-violation translated to <see cref="ConversationConcurrencyConflictException"/>
/// (`ConversationRepository`'s own remarks). Caught here, once: the loser's own copy is worthless
/// (its id lost the race), but the winner's row is already committed and visible to a fresh read -
/// so the loser simply returns it, exactly as if <c>existing is not null</c> had been true from the
/// start. Not a retry-and-reapply like `MarkConversationReadHandler`'s own shape: there is nothing
/// to reapply, "start a conversation" has no decision left to make once one already exists.</para>
/// </summary>
public sealed class StartConversationHandler(
    IVisitorRepository visitors,
    IConversationRepository conversations,
    GetSiteConfigByIdHandler siteConfig,
    IRateLimiter rateLimiter,
    ConversationCreateRateLimitOptions rateLimitOptions,
    IClock clock,
    IIdGenerator idGenerator,
    IVisitorEmojiPairGenerator emojiPairs)
{
    public async Task<Result<StartConversationResult>> HandleAsync(
        StartConversation command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var visitor = await visitors.GetByIdAsync(command.VisitorId, cancellationToken);
        if (visitor is null)
        {
            visitor = new Visitor(command.VisitorId, command.SiteId, now);
            // `25-56` decision 5: assigned here, at first contact, and never again - this is one of
            // exactly two places in this codebase that ever calls AssignEmojiPair (Visitor's own
            // remarks name the other, ReceiveChannelMessageHandler).
            var (creature, food) = emojiPairs.NextPair();
            visitor.AssignEmojiPair(creature, food);
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

        // `23-76`: only the genuinely-new-conversation path spends this budget - resuming an existing
        // conversation (the branch just above) is not what resets `23-75`'s per-conversation attachment
        // budget, so it costs nothing here. Per-visitor first, per-site last - the same ordering
        // convention `CreateAttachmentHandler`'s own remarks state ("a caller who was never going to
        // pass their own limit should not also spend a share of the site's budget finding that out").
        // See `ConversationCreateRateLimitOptions`'s own remarks on why the per-visitor bucket alone is
        // a speed bump, not a limit - a fresh `VisitorId` is free to mint, so only the per-site bucket
        // actually bounds the flood this item exists to stop; both are still checked, per this item's
        // own literal text.
        var visitorLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"conversation-create:visitor:{command.VisitorId.Value}"),
            new RateLimitRule(rateLimitOptions.PerVisitorCapacity, rateLimitOptions.PerVisitorRefillPerSecond),
            cancellationToken);
        if (!visitorLimit.Allowed)
        {
            return ConversationErrors.ConversationCreateRateLimited(visitorLimit.RetryAfter);
        }

        var siteLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"conversation-create:site:{command.SiteId.Value}"),
            new RateLimitRule(rateLimitOptions.PerSiteCapacity, rateLimitOptions.PerSiteRefillPerSecond),
            cancellationToken);
        if (!siteLimit.Allowed)
        {
            return ConversationErrors.ConversationCreateRateLimited(siteLimit.RetryAfter);
        }

        var config = await siteConfig.HandleAsync(new GetSiteConfigById.GetSiteConfigById(command.SiteId), cancellationToken);
        var attachmentUploadGrantedByDefault = config?.WidgetAllowAttachmentUploadsByDefault ?? false;

        var conversationId = new ConversationId(idGenerator.NewId(now));
        var conversation = Conversation.Start(
            conversationId, command.SiteId, command.VisitorId, now, command.Source, attachmentUploadGrantedByDefault);
        try
        {
            await conversations.SaveAsync(conversation, cancellationToken);
        }
        catch (ConversationConcurrencyConflictException)
        {
            // Lost the race - see this class's own remarks. The winner committed first, so it is
            // there to be read now.
            var winner = await conversations.GetActiveForVisitorAsync(command.VisitorId, cancellationToken);
            if (winner is null)
            {
                // Unreachable by construction: the constraint that produced this exception only fires
                // when a matching row already exists. Rethrown rather than silently treated as "no
                // conversation" - the same "do not paper over a broken invariant" choice
                // ConversationRepository's own remarks make for a null it cannot explain either.
                throw;
            }

            return new StartConversationResult(winner.Id, IsNew: false, winner.HasAttachmentUploadGrant);
        }

        return new StartConversationResult(conversation.Id, IsNew: true, conversation.HasAttachmentUploadGrant);
    }
}
