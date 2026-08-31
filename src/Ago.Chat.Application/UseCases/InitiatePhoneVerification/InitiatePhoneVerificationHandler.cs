using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.InitiatePhoneVerification;

/// <summary>
/// `14-15`/`adr/0079`: the send-triggering half of phone verification. Validates and canonicalises the
/// phone number, rate-limits, persists a fresh <see cref="PendingPhoneVerification"/> plus its outbox row
/// in one <see cref="IPendingPhoneVerificationRepository.SaveAsync"/> call, and returns immediately - the
/// actual paid SMS/voice send happens later, in `Ago.Chat.Worker`'s own
/// <c>PhoneVerificationDeliveryConsumer</c>, never inline here (CLAUDE.md rule 4; this type's own
/// <see cref="IPhoneVerificationSender"/> remarks state the same rule again for this specific call).
///
/// <para><b>Visitor-only - no <c>HandleAsOperatorAsync</c> twin, unlike <c>CreateAttachmentHandler</c>/
/// <c>RecordVisitorContactDetailHandler</c>.</b> Both of those exist because an operator genuinely
/// performs the analogous action themselves (uploading an attachment, writing down a fact a visitor said
/// out loud). Proving control of a phone number is structurally different: the code is read off a call or
/// an SMS arriving on the visitor's own phone, so the visitor is always the one who can meaningfully act
/// on either half of this flow (<see cref="ConfirmPhoneVerification.ConfirmPhoneVerificationHandler"/>'s
/// own identical scoping note). An operator-relay entry point (an operator typing the number on the
/// visitor's behalf, the same shape `RecordVisitorContactDetail` uses) was considered and deliberately
/// left out of this item's scope - nothing in the backlog file's own Done-when needs it, and adding an
/// unused second path would be exactly the premature generalisation `clean-architecture.md` warns
/// against. It is a small, additive follow-up if a real operator workflow ever asks for it.</para>
///
/// <para><b>Reuses <see cref="IPendingChannelLinkCodeGenerator"/> rather than a new generator port.</b>
/// This item's own backlog file names the same reasoning that port's own remarks already give for
/// itself: a six-digit numeric code is exactly as human-typeable read off a phone call or an incoming SMS
/// as it is relayed through a chat window, and inventing a second, materially identical port for the
/// identical "deliberately low-entropy, must be retypeable by hand" shape would only create two things to
/// keep in sync for no behavioural difference.</para>
/// </summary>
public sealed class InitiatePhoneVerificationHandler(
    IConversationRepository conversations,
    IPendingPhoneVerificationRepository pendingVerifications,
    IPendingChannelLinkCodeGenerator codeGenerator,
    IRateLimiter rateLimiter,
    IOutboxWriter outbox,
    PhoneVerificationOptions options,
    PhoneVerificationRateLimitOptions rateLimitOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<InitiatedPhoneVerification>> HandleAsVisitorAsync(
        InitiatePhoneVerificationAsVisitor command, CancellationToken cancellationToken)
    {
        PhoneNumber phone;
        try
        {
            phone = new PhoneNumber(command.Phone);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.PhoneVerificationInvalidNumber(ex.Message);
        }

        // Phone bucket first, then visitor - both checkable before any database read - the ordering
        // `PhoneVerificationRateLimitOptions`'s own remarks reason through for this item's two distinct
        // threats.
        var phoneLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"phone-verification:phone:{phone.Value}"),
            new RateLimitRule(rateLimitOptions.PerPhoneCapacity, rateLimitOptions.PerPhoneRefillPerSecond),
            cancellationToken);
        if (!phoneLimit.Allowed)
        {
            return ConversationErrors.PhoneVerificationRateLimited(phoneLimit.RetryAfter);
        }

        var visitorLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"phone-verification:visitor:{command.RequestedBy.Value}"),
            new RateLimitRule(rateLimitOptions.PerVisitorCapacity, rateLimitOptions.PerVisitorRefillPerSecond),
            cancellationToken);
        if (!visitorLimit.Allowed)
        {
            return ConversationErrors.PhoneVerificationRateLimited(visitorLimit.RetryAfter);
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

        // Per-site last, after the caller's own buckets - the identical "a caller who was never going to
        // pass their own bucket should not also spend a share of the site's budget finding that out"
        // ordering `CreateAttachmentHandler`'s own remarks state.
        var siteLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"phone-verification:site:{conversation.SiteId.Value}"),
            new RateLimitRule(rateLimitOptions.PerSiteCapacity, rateLimitOptions.PerSiteRefillPerSecond),
            cancellationToken);
        if (!siteLimit.Allowed)
        {
            return ConversationErrors.PhoneVerificationRateLimited(siteLimit.RetryAfter);
        }

        var now = clock.UtcNow;
        var code = codeGenerator.NewCode();
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));

        var verification = PendingPhoneVerification.Request(
            new PendingPhoneVerificationId(idGenerator.NewId(now)), conversation.SiteId, conversation.VisitorId,
            phone, code, codeHash, options.DefaultDeliveryMethod, now, options.ValidFor, options.MaxAttempts);

        var issued = verification.DomainEvents.OfType<PhoneVerificationCodeIssued>().Single();
        outbox.Enqueue(PhoneVerificationCodeIssuedMapper.ToEnvelope(issued, idGenerator));
        verification.ClearDomainEvents();

        // Verification row and outbox row share this one SaveChangesAsync - CLAUDE.md rule 4's "same
        // transaction" satisfied the identical way `RemoveOperatorHandler`'s own remarks describe: one
        // aggregate, one outbox row, one DbContext, one commit.
        await pendingVerifications.SaveAsync(verification, cancellationToken);

        return new InitiatedPhoneVerification(verification.Id.Value, verification.ExpiresAt, verification.DeliveryMethod.ToString());
    }
}
