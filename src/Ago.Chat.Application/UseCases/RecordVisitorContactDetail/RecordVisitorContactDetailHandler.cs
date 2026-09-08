using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RecordVisitorContactDetail;

/// <summary>
/// `14-14`/`23-09`/`adr/0079` section 6.
///
/// <para><b>Gated on <see cref="Permission.ConversationSend"/> for the operator path, the backlog
/// item's own instruction, for the identical reason <c>RequestChannelLinkFromConsoleHandler</c>'s own
/// remarks already give for reusing it: "recording a fact told to the operator inside a conversation is
/// not more sensitive than replying in it." No new, dedicated permission - unlike
/// <see cref="Permission.ChannelIdentityUnlink"/>, there is no routing capability being protected here,
/// only a note; the backlog item's own Scope section names this explicitly.</b></para>
///
/// <para>Tenant scope for the operator path is checked the same way
/// <c>RequestChannelLinkFromConsoleHandler</c> checks it - <see cref="IConversationRepository.GetByIdAsync"/>
/// (unscoped by site) plus an explicit <c>conversation.SiteId != command.SiteId</c> comparison, not
/// <see cref="ListChannelIdentitiesForVisitor.ListChannelIdentitiesForVisitorHandler"/>'s narrower
/// assigned-operator check. That check exists there because unlinking/relinking a channel identity is
/// scoped to whoever currently owns the conversation; recording a fact a visitor just said, like
/// requesting a link code, is a site-wide capability every operator holding
/// <see cref="Permission.ConversationSend"/> already has for this conversation's own send path, so
/// narrowing it further here would add a restriction the backlog item never asked for.</para>
///
/// <para><b>`23-09` adds <see cref="HandleAsVisitorAsync"/> beside the operator path above - one
/// handler, two entry points, the identical shape <c>CreateAttachmentHandler</c>/
/// <c>GetConversationHistoryHandler</c> already establish for themselves.</b> A visitor is outside the
/// RBAC model entirely (`adr/0016`), so there is no permission to check; what replaces it is the same
/// participant comparison every other visitor entry point in this codebase uses -
/// <c>conversation.VisitorId == command.RequestedBy</c>, read from the signed visitor token, never
/// from a conversation id the caller merely names (`TenantScopeExemptions.cs`'s own entry for this
/// method states the same rule again for the tenant-scope auditor). This is a genuinely different
/// authorization shape from the operator path above, not a copy of it with the permission check
/// removed - the backlog item's own "a visitor-authenticated write path is not an operator one" is
/// the reason <see cref="RecordVisitorContactDetailAsVisitor"/> is its own command type rather than a
/// reused one with a nullable <see cref="OperatorId"/>.</para>
///
/// <para><b>`24-05`: both entry points now check <see cref="ConsentSatisfiedAsync"/> right before
/// building the row, never before.</b> The gate attaches to <em>this write</em> - handing over a
/// contact detail - never to the conversation itself: nothing above either method's own existing
/// checks changes, a visitor who has not consented can still open the panel, read the auto-reply, and
/// keep talking; only this one write is refused, with a `409` a caller resolves by recording the
/// consent (`RecordVisitorConsentHandler`) and retrying the identical request. Checked on <b>both</b>
/// paths deliberately, not only the visitor's own form (`23-09`): the backlog item's own "Depends on"
/// names `23-09` and `23-10` together as "the acts this consent actually attaches to," and `23-10`'s
/// operator-promoted row is still a contact detail newly made findable and actionable, the same
/// personal-data-shape argument `23-09`'s own backlog item already made for why a visitor-typed number
/// is not exempt just because it also sits in the transcript. This is a real, judgment-call reading of
/// an item that did not spell out which path(s) it meant - stated here rather than silently
/// assumed.</para>
///
/// <para><b>`23-59`/`adr/0147`: both entry points now stage <see cref="Ago.Chat.Contracts.ContactCollected"/>
/// through the outbox, before the write it describes - the same "enqueue, then call the repository's
/// own SaveAsync" ordering <c>UpdateContactVisibilityHandler</c>'s own remarks establish, so the one
/// <c>SaveChangesAsync</c> that write makes is what commits the row and the event together
/// (`CLAUDE.md` rule 4).</b> This handler does not know, and must not come to know, whether AGO
/// Calendar or any other product exists - the identical ignorance <c>RoleRepository.AddPermissionsAsync</c>'s
/// own remarks describe for a module grant. <see cref="IOutboxWriter"/> is injected directly rather
/// than the write moving into Infrastructure, the same "dependency-free platform port, no reason to
/// leave Application" shape <c>ConfirmAttachmentHandler</c>'s own remarks already give for
/// themselves.</para>
/// </summary>
public sealed class RecordVisitorContactDetailHandler(
    IConversationRepository conversations,
    IVisitorContactDetailRepository contactDetails,
    ISiteRepository sites,
    IAcceptanceRepository acceptances,
    IPermissionChecker permissions,
    IRateLimiter rateLimiter,
    ContactDetailRateLimitOptions rateLimitOptions,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<RecordedVisitorContactDetail>> HandleAsOperatorAsync(
        RecordVisitorContactDetailAsOperator command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages in this conversation.");
        }

        if (!TryParseKind(command.Kind, out var kind))
        {
            return ConversationErrors.ContactDetailInvalidKind(command.Kind);
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null || conversation.SiteId != command.SiteId)
        {
            // Wrong-tenant reads like no row - the same info-hiding shape every cross-tenant guard in
            // this codebase already uses (ConversationErrors.NotFound's own callers).
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (!await ConsentSatisfiedAsync(conversation.SiteId, conversation.VisitorId, cancellationToken))
        {
            return ConversationErrors.VisitorContactDetailConsentRequired();
        }

        var now = clock.UtcNow;
        VisitorContactDetail detail;
        try
        {
            detail = VisitorContactDetail.Record(
                new VisitorContactDetailId(idGenerator.NewId(now)), conversation.VisitorId, kind, command.Value,
                command.RequestedBy, now);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ContactDetailInvalid(ex.Message);
        }

        outbox.Enqueue(ContactCollectedMapper.ToEnvelope(detail, conversation.SiteId, now, idGenerator));
        await contactDetails.SaveAsync(detail, cancellationToken);

        return ToResult(detail);
    }

    /// <summary>`23-09`. See this class's own remarks above for why this is a genuinely different
    /// authorization shape, not the operator path with its permission check removed.</summary>
    public async Task<Result<RecordedVisitorContactDetail>> HandleAsVisitorAsync(
        RecordVisitorContactDetailAsVisitor command, CancellationToken cancellationToken)
    {
        if (!TryParseKind(command.Kind, out var kind))
        {
            return ConversationErrors.ContactDetailInvalidKind(command.Kind);
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

        // Visitor bucket first, then site - the same "a caller who was never going to pass their own
        // bucket should not also spend a share of the site's budget finding that out" ordering
        // `CreateAttachmentHandler`'s own remarks give for its own two buckets.
        var visitorLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"contact-detail:visitor:{command.RequestedBy.Value}"),
            new RateLimitRule(rateLimitOptions.PerVisitorCapacity, rateLimitOptions.PerVisitorRefillPerSecond),
            cancellationToken);
        if (!visitorLimit.Allowed)
        {
            return ConversationErrors.RateLimited(visitorLimit.RetryAfter);
        }

        var siteLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"contact-detail:site:{conversation.SiteId.Value}"),
            new RateLimitRule(rateLimitOptions.PerSiteCapacity, rateLimitOptions.PerSiteRefillPerSecond),
            cancellationToken);
        if (!siteLimit.Allowed)
        {
            return ConversationErrors.RateLimited(siteLimit.RetryAfter);
        }

        if (!await ConsentSatisfiedAsync(conversation.SiteId, conversation.VisitorId, cancellationToken))
        {
            return ConversationErrors.VisitorContactDetailConsentRequired();
        }

        var now = clock.UtcNow;
        VisitorContactDetail detail;
        try
        {
            detail = VisitorContactDetail.RecordFromVisitor(
                new VisitorContactDetailId(idGenerator.NewId(now)), conversation.VisitorId, kind, command.Value, now);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ContactDetailInvalid(ex.Message);
        }

        outbox.Enqueue(ContactCollectedMapper.ToEnvelope(detail, conversation.SiteId, now, idGenerator));
        await contactDetails.SaveAsync(detail, cancellationToken);

        return ToResult(detail);
    }

    /// <summary>`24-05`'s own gate: <see langword="true"/> when the site does not require contact
    /// consent at all (every site before this item, and every site since that has not opted in - the
    /// unchanged-default requirement), or when this visitor already holds a recorded acceptance of
    /// this site's own contact-consent document, any version - <see cref="SiteConsentDocumentKey.For"/>
    /// derives the same key <c>RecordVisitorConsentHandler</c> writes against, so the two can never
    /// drift apart on how the key is spelled. A site that requires consent but has not published its
    /// own document yet reads as "not satisfied" here too - the honest answer, since nothing this
    /// visitor could do would ever produce a matching acceptance for a key nothing was ever published
    /// under (`GetConsentRequirementHandler`'s own read surfaces that gap to the widget as "required,
    /// not yet available" rather than this handler guessing at a friendlier failure).</summary>
    private async Task<bool> ConsentSatisfiedAsync(SiteId siteId, VisitorId visitorId, CancellationToken cancellationToken)
    {
        var site = await sites.GetByIdAsync(siteId, cancellationToken);
        if (site is null || !site.WidgetConfig.RequireContactConsent)
        {
            return true;
        }

        var contactKey = SiteConsentDocumentKey.For(siteId, VisitorConsentPurpose.Contact);
        var accepted = await acceptances.GetForSubjectAsync(AcceptanceSubjectKind.Visitor, visitorId.Value, cancellationToken);
        return accepted.Any(a => a.DocumentKey == contactKey);
    }

    private static bool TryParseKind(string raw, out VisitorContactDetailKind kind) =>
        Enum.TryParse(raw, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    private static RecordedVisitorContactDetail ToResult(VisitorContactDetail detail) =>
        new(
            detail.Id.Value, detail.VisitorId.Value, detail.Kind.ToString(), detail.Value,
            detail.RecordedByOperatorId?.Value, detail.Source.ToString(), detail.Verified, detail.RecordedAt);
}
