using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Application.UseCases.CloseConversationAsSpam;

/// <summary>
/// `23-69`: an operator's "close as spam" - one act, per this item's own Done-when, that both closes
/// the conversation exactly as <c>CloseConversationHandler</c> already does and writes a time-windowed
/// <c>visitor_restrictions</c> row against the visitor (`IVisitorRestrictionRepository`), so every
/// conversation that same visitor opens on this site during the window is silently kept from ever
/// reaching an operator too (`StartConversationHandler`'s own remarks on <c>RoutingSuppressedAt</c>).
///
/// <para><b>A distinct handler, not a boolean parameter on <c>CloseConversationHandler</c>.</b> Three
/// reasons, together: (1) this item's own gate is a different, dedicated permission
/// (<see cref="Permission.ConversationMarkSpam"/>, not <see cref="Permission.ConversationClose"/> -
/// that permission's own remarks give the granular-permission reasoning in full), so an ordinary
/// close and a spam-close are already answering two different authorization questions before either
/// touches the conversation; (2) the consequence is not a detail of closing, it reaches outside this
/// conversation entirely, onto a person, for a stated duration - exactly the shape this codebase
/// already keeps as its own dedicated command elsewhere (`SetUnconditionalModuleGrantAsOwner` kept
/// separate from `GrantModuleQuantityAsOwner` for the identical "a genuinely distinct act, not a flag
/// on an existing one" reason); (3) it keeps `CloseConversationHandler` itself, its own tests, and
/// every existing caller of plain `CloseConversation` completely untouched - this item's own
/// Depends-on note is explicit that `24-10`'s own mechanism (and, by the same logic, this codebase's
/// existing close path) stays exactly as it is, additive rather than repurposed. The trade this makes
/// is duplicating `CloseConversationHandler`'s own close-and-save shape below rather than sharing it -
/// accepted deliberately: `BlockConversationHandler`/`UnblockConversationHandler` already duplicate a
/// near-identical shape as siblings rather than factoring out a shared helper, and doing the same here
/// keeps each handler readable as the complete story of its own one act, the same tradeoff this
/// codebase has already made once for a directly comparable pair.
/// </para>
///
/// <para><b>The restriction write happens strictly after the close is durably saved</b>, the same
/// "never claim a consequence that has not actually landed" ordering `CloseConversationHandler`'s own
/// remarks already use for releasing an operator's capacity claim after, never before, its own save -
/// restated here for a second post-close consequence on the same request. Unlike that capacity release,
/// this write is not wrapped in its own contention handling: <c>IVisitorRestrictionRepository.RestrictAsync</c>
/// is a plain, unconditional <c>INSERT</c> with nothing to lose a race against (this interface's own
/// remarks on why no uniqueness constraint exists here) - a failure here is an ordinary infrastructure
/// fault, not an expected, named contention outcome the way `OperatorCapacityContentionException` is,
/// so it is left to propagate as an ordinary `500` rather than being swallowed into a false "success."
/// </para>
/// </summary>
public sealed class CloseConversationAsSpamHandler(
    IConversationRepository conversations,
    IConversationAssignmentLog assignmentLog,
    IVisitorRestrictionRepository restrictions,
    IPermissionChecker permissions,
    IOperatorCapacity capacity,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock,
    ConversationSpamMuteOptions muteOptions,
    ILogger<CloseConversationAsSpamHandler> logger)
{
    public async Task<Result<CloseConversationAsSpamResult>> HandleAsync(
        CloseConversationAsSpam command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.SiteId, Permission.ConversationMarkSpam, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to mark conversations as spam for this site.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        try
        {
            return await CloseAndRestrictAsync(conversation, command, cancellationToken);
        }
        catch (ConversationConcurrencyConflictException)
        {
            // The same single-retry shape `CloseConversationHandler`'s own remarks explain in full -
            // a concurrent writer (typically a message send) landed between the read above and the
            // save below, not that closing itself is wrong.
            var fresh = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
            if (fresh is null)
            {
                return ConversationErrors.NotFound(command.ConversationId.Value);
            }

            try
            {
                return await CloseAndRestrictAsync(fresh, command, cancellationToken);
            }
            catch (ConversationConcurrencyConflictException)
            {
                return ConversationErrors.ConcurrencyConflict(command.ConversationId.Value);
            }
        }
    }

    private async Task<Result<CloseConversationAsSpamResult>> CloseAndRestrictAsync(
        Conversation conversation, CloseConversationAsSpam command, CancellationToken cancellationToken)
    {
        if (conversation.OperatorId != command.OperatorId)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        var now = clock.UtcNow;
        bool consumedCapacityClaim;
        try
        {
            consumedCapacityClaim = conversation.Close(now);
        }
        catch (InvalidConversationStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }

        var domainEvent = conversation.DomainEvents.OfType<ConversationClosed>().Single();
        outbox.Enqueue(ConversationClosedMapper.ToEnvelope(domainEvent, idGenerator));
        conversation.ClearDomainEvents();

        await assignmentLog.CloseOpenAsync(conversation.Id, now, cancellationToken);

        // May throw ConversationConcurrencyConflictException - left to propagate to HandleAsync's
        // retry wrapper, the identical shape CloseConversationHandler's own remarks describe.
        await conversations.SaveAsync(conversation, cancellationToken);

        if (consumedCapacityClaim)
        {
            try
            {
                await capacity.ReleaseAsync(command.OperatorId, cancellationToken);
            }
            catch (OperatorCapacityContentionException ex)
            {
                // The identical residual CloseConversationHandler's own remarks already accept in
                // full for this same failure - the close is already committed, so this stays a
                // successful request with one leaked capacity slot, recovered by `4-04`'s disconnect
                // sweep.
                logger.LogWarning(
                    ex,
                    "Conversation {ConversationId} closed as spam, but operator {OperatorId}'s capacity slot could not be released after {Attempts} attempt(s); one slot leaks until that operator next disconnects.",
                    command.ConversationId.Value,
                    command.OperatorId.Value,
                    ex.Attempts);
            }
        }

        // Strictly after the save - see this handler's own remarks on why the restriction must never
        // be written for a close that did not actually land.
        var expiresAt = now.Add(muteOptions.DefaultDuration);
        var restrictionRecordId = idGenerator.NewId(now);
        await restrictions.RestrictAsync(
            command.SiteId,
            conversation.VisitorId,
            command.OperatorId,
            VisitorRestrictionKind.Spam,
            expiresAt,
            conversation.Id,
            restrictionRecordId,
            now,
            cancellationToken);

        return new CloseConversationAsSpamResult(expiresAt);
    }
}

/// <summary>The wire shape of a successful "close as spam" - the one new fact the console needs beyond
/// an ordinary close's own `204`: when the resulting mute lifts on its own, so an operator sees the
/// window they just started rather than having to know <see cref="ConversationSpamMuteOptions.DefaultDuration"/>
/// by heart.</summary>
public sealed record CloseConversationAsSpamResult(DateTimeOffset MutedUntil);
