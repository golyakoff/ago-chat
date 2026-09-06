using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RecordVisitorConsent;

/// <summary>
/// `24-05`: the one entry point that turns a visitor's own click into an <see cref="AcceptanceRecord"/>.
/// Visitor-only - unlike <c>RecordVisitorContactDetailHandler</c>, there is no operator-initiated twin
/// here at all, deliberately: an operator recording a visitor's consent *for* them would be exactly the
/// self-service act this item's own crux forbids (`docs/backlog/24-05-*.md`'s own framing - a consent
/// is the caller asserting a fact about themselves, the identical shape
/// <c>RecordAcceptanceHandler</c>'s own remarks already give for every subject kind).
///
/// <para>Resolves the document key and its current version itself
/// (<see cref="SiteConsentDocumentKey.For"/> plus <see cref="IDocumentRepository.FindCurrentAsync"/>) -
/// the caller never supplies either, the same "never trust the caller to have already resolved what it
/// is accepting" reasoning <see cref="RecordVisitorConsent"/>'s own remarks give.</para>
/// </summary>
public sealed class RecordVisitorConsentHandler(
    IConversationRepository conversations,
    IDocumentRepository documents,
    IAcceptanceRepository acceptances,
    IRateLimiter rateLimiter,
    ConsentRateLimitOptions rateLimitOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<RecordedVisitorConsent>> HandleAsync(RecordVisitorConsent command, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<VisitorConsentPurpose>(command.Purpose, ignoreCase: true, out var purpose) || !Enum.IsDefined(purpose))
        {
            return PublishedDocumentErrors.InvalidPurpose(
                $"'{command.Purpose}' is not a valid consent purpose - expected '{nameof(VisitorConsentPurpose.Contact)}' or "
                + $"'{nameof(VisitorConsentPurpose.Marketing)}'.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.VisitorId != command.RequestedBy)
        {
            return ConversationErrors.Forbidden("This visitor is not a participant of this conversation.");
        }

        // Visitor bucket first, then site - the identical "a caller who was never going to pass their
        // own bucket should not also spend a share of the site's budget finding that out" ordering
        // RecordVisitorContactDetailHandler.HandleAsVisitorAsync already uses for its own two buckets.
        var visitorLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"consent:visitor:{command.RequestedBy.Value}"),
            new RateLimitRule(rateLimitOptions.PerVisitorCapacity, rateLimitOptions.PerVisitorRefillPerSecond),
            cancellationToken);
        if (!visitorLimit.Allowed)
        {
            return ConversationErrors.ConsentRateLimited(visitorLimit.RetryAfter);
        }

        var siteLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"consent:site:{conversation.SiteId.Value}"),
            new RateLimitRule(rateLimitOptions.PerSiteCapacity, rateLimitOptions.PerSiteRefillPerSecond),
            cancellationToken);
        if (!siteLimit.Allowed)
        {
            return ConversationErrors.ConsentRateLimited(siteLimit.RetryAfter);
        }

        var documentKey = SiteConsentDocumentKey.For(conversation.SiteId, purpose);
        var current = await documents.FindCurrentAsync(documentKey, cancellationToken);
        if (current is null)
        {
            return PublishedDocumentErrors.ConsentDocumentUnavailable(documentKey);
        }

        var now = clock.UtcNow;
        AcceptanceRecord record;
        try
        {
            record = AcceptanceRecord.ForVisitor(
                new AcceptanceRecordId(idGenerator.NewId(now)), command.RequestedBy, documentKey, current.Version, now,
                command.ClientIp, command.UserAgent);
        }
        catch (ArgumentException ex)
        {
            return AcceptanceErrors.Invalid(ex.Message);
        }

        await acceptances.SaveAsync(record, cancellationToken);

        return new RecordedVisitorConsent(record.Id.Value, record.DocumentKey, record.DocumentVersion, record.AcceptedAt);
    }
}
