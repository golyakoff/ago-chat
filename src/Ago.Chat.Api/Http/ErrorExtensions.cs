using System.Globalization;
using Ago.Platform.Kernel;

namespace Ago.Chat.Api.Http;

/// <summary>
/// `api-design.md`: "Errors are RFC 7807 problem details with a stable machine-readable `type`...
/// clients branch on `type`, never on the message." <see cref="Ago.Chat.Application.UseCases.ConversationErrors"/>'s
/// codes already are that stable vocabulary (shared with every hub's `HubException` today) - this is
/// the first HTTP endpoint file to translate one into a response, so the mapping lives here rather
/// than beside `AuthEndpoints`, which never went through `Result&lt;T&gt;`/`Error` at all (`3-05`'s
/// rate limit and site lookup built their `Results.Problem` calls by hand, before this vocabulary had
/// an HTTP-facing consumer).
/// </summary>
public static class ErrorExtensions
{
    /// <summary>
    /// <paramref name="retryAfter"/>: `ago-root#353`. Optional and caller-supplied, never derived in
    /// here - this method has no way to tell a rate-limited <see cref="Error"/> apart from any other
    /// (the vocabulary is <c>(Code, Message)</c>, nothing more), so the five endpoints that own a
    /// <c>*.RateLimited</c> code compute their own conservative wait via <see cref="RateLimitRetryAfter.Conservative"/>,
    /// from configuration they already hold, and pass it in only for that one code. Every other call
    /// site keeps calling <c>error.ToProblem(httpContext)</c> unchanged.
    /// </summary>
    public static IResult ToProblem(this Error error, HttpContext httpContext, TimeSpan? retryAfter = null)
    {
        var statusCode = error.Code switch
        {
            "Conversation.NotFound" or "Attachment.NotFound" or "WebhookEndpoint.NotFound" or "Site.NotFound"
                or "ChannelCredential.NotFound" or "OperatorInvite.NotFound" or "Export.NotFound"
                // `25-85`: the same 404 group as OperatorInvite.NotFound right above, deliberately -
                // ConversationErrors.OperatorInviteNoAutoRedeemablePendingInvite's own remarks: "nothing
                // to auto-redeem" is not a caller mistake worth a scarier status than a code that does
                // not exist.
                or "OperatorInvite.NoAutoRedeemablePendingInvite"
                // `18-02`: deliberately the same 404 group as Conversation.NotFound, not its own
                // bucket - ConversationErrors.TransferTargetNotEligible's own remarks on why a
                // wrong-tenant or ineligible operator must read exactly like one that does not exist.
                or "Conversation.TransferTargetNotEligible"
                // `18-04`: same "wrong tenant reads like no such row" info-hiding shape - a tag id
                // from a different site is indistinguishable from one that never existed.
                or "Tag.NotFound"
                // `14-12`: the identical "wrong tenant reads like no such row" shape - a channel
                // identity id from a different site is indistinguishable from one that never existed.
                or "ChannelIdentity.NotFound"
                // `14-14`: the identical "wrong visitor reads like no such row" shape - a contact
                // detail id belonging to a different visitor is indistinguishable from one that never
                // existed (DeleteVisitorContactDetailHandler's own remarks).
                or "VisitorContactDetail.NotFound"
                // `14-13`: the same info-hiding shape once more - naming a real id that exists but is
                // not eligible (someone else's identity, or unlinked) must read exactly like naming one
                // that never existed at all, ConversationErrors.ChannelIdentityNotEligibleForPreference's
                // own remarks.
                or "ChannelIdentity.NotEligibleForPreference"
                // `14-15`: the identical "wrong tenant/visitor reads like no such row" shape once more -
                // a pending phone verification id from a different site or a different visitor is
                // indistinguishable from one that never existed.
                or "PhoneVerification.NotFound"
                // `22-11`: an operator tried to rotate/revoke/check a module registration for a site
                // that does not have that module enabled - the same "nothing to act on" shape every
                // other NotFound code in this group already gets.
                or "Module.NotEnabled"
                // `23-72`: found while wiring this item's own new route - `RemoveOperatorHandler`/
                // `ToggleOperatorSeatHandler` have returned this code since `13-03` and it was never
                // mapped here, so naming a nonexistent operator id on either existing route has always
                // fallen through to this switch's `500` default rather than the `404` a caller's own
                // mistake deserves. `ChangeOperatorRoleHandler` returns the identical code
                // (`ConversationErrors.OperatorNotFound`) on its own new route, so fixing the mapping is
                // this item's own concern, not a drive-by fix of the older two handlers - see this
                // item's own report for the two adjacent codes (`Operator.AlreadyRemoved`,
                // `Operator.SeatLimitReached`) left exactly as found.
                or "Operator.NotFound"
                // `25-98`: this whole switch case audited end to end - four more codes reached a real
                // HTTP endpoint's `ToProblem` and had never been given a line here, all falling
                // through to the `500` default below.
                //
                // `Billing.SubscriptionNotFound` - the same plain "no row" shape every other code in
                // this group already has (ConversationErrors.BillingSubscriptionNotFound's own remarks).
                or "Billing.SubscriptionNotFound"
                // `Document.NotFound` - "no version - specific or current - exists under the requested
                // key", PublishedDocumentErrors.NotFound's own remarks; the same plain "no row" shape.
                or "Document.NotFound"
                // `MessageArchive.NotFound` - ConversationErrors.MessageArchiveNotFound's own remarks
                // name it "the identical wrong-tenant-reads-like-no-such-row cross-tenant guard
                // ExportNotFound already establishes", so it belongs in this exact group, not a new one.
                or "MessageArchive.NotFound"
                // `ModuleTaskChannelPriority.ChannelNotEligible` - ConversationErrors.ModuleTaskChannelNotEligible's
                // own remarks name it "the same never-an-arbitrary-id invariant ChannelIdentityNotEligibleForPreference
                // already enforces" - and that code sits in this identical info-hiding group two lines up
                // in this same case, for the identical reason.
                or "ModuleTaskChannelPriority.ChannelNotEligible" => StatusCodes.Status404NotFound,
            // `23-71`: the identical shape as Conversation.Forbidden right above - a real permission
            // holder refused for a second, orthogonal reason (holding no seat), not a malformed
            // request or a conflict with anything concurrent. ConversationErrors.OperatorHasNoSeat's
            // own remarks.
            // `23-78`: the visitor-side gate itself - a real refusal, not a malformed request or a
            // conflict with anything concurrent, the identical shape `Conversation.Forbidden` already
            // has for itself (`ConversationErrors.AttachmentUploadNotGranted`'s own remarks on why the
            // message names no remedy).
            // `22-08`: the operator-send suspension gate - a real refusal (the account is currently
            // suspended), not a malformed request or a conflict with anything concurrent, reached
            // today only from OperatorHub's own HubException translation but mapped here too for
            // completeness, the identical reasoning Conversation.OperatorHasNoSeat's own remarks give.
            // `25-73`: the signed-in caller's own token email does not match the invite this code
            // names - a real refusal (this identity is not who this invite was sent to), not a
            // malformed request or a conflict with anything concurrent, the identical shape
            // `Conversation.Forbidden` already has for itself.
            // `25-83`: the hard download-block threshold - the identical shape `TenantSuspension.CannotSend`
            // right beside it already has: a real, account-wide refusal (this tenant's own monthly
            // download allowance is spent), not a malformed request or a conflict with anything
            // concurrent, and not tied to the caller's own individual permission grant the way
            // `Conversation.Forbidden` is. Found by code inspection while fixing the sibling gap right
            // below (`Site.DownloadBlockExemptionReasonRequired`, caught live by this item's own
            // `OwnerDownloadBlockExemptionEndpointTests`) and checked for here too -
            // `GetAttachmentDownloadUrlHandler` has returned this code since it was written, and it
            // fell through to this switch's `500` default the whole time (no test in this codebase
            // exercises `GET /api/v1/attachments/{id}` over real HTTP yet, for any outcome - a
            // follow-up item, not this one, closes that), exactly the `Operator.NotFound` gap `23-72`'s
            // own remarks above describe for a different code.
            // `25-98`: `AiAddOn.Forbidden` - AiAddOnErrors' own remarks give it no shape of its own; it
            // is the same "Permission.SiteConfigure holder refused" real permission failure this whole
            // group already carries for every other resource (the identical check EnableAiAddOnHandler/
            // AcceptAiAddOnAgreementHandler/DeclareAiProcessingBasisHandler/DisableAiAddOnHandler each run
            // first, before any of this item's own four new gaps below). Reached a real `ToProblem` call
            // (`AiAddOnEndpoints`) and had never been given a line here, falling through to the `500`
            // default below - the same shape `23-72`'s own `Operator.NotFound` entry above describes.
            "Conversation.Forbidden" or "Conversation.OperatorHasNoSeat" or "Attachment.UploadNotGranted"
                or "TenantSuspension.CannotSend" or "OperatorInvite.EmailMismatch"
                or "Attachment.DownloadBlocked" or "AiAddOn.Forbidden" => StatusCodes.Status403Forbidden,
            // `25-98`: `Attachment.ConversationBudgetExceeded`/`Attachment.SiteBudgetExceeded` join
            // `Attachment.TooLarge` here - both codes' own remarks call themselves "distinct from
            // `AttachmentTooLarge` ... even though the shape is identical": a declared size that will
            // not fit, just measured against a running total instead of the one file's own ceiling.
            // Reached a real `ToProblem` call (`AttachmentEndpoints`/`CreateAttachmentHandler`) and had
            // never been given a line here.
            "Attachment.TooLarge" or "Attachment.ConversationBudgetExceeded" or "Attachment.SiteBudgetExceeded"
                => StatusCodes.Status413PayloadTooLarge,
            "Attachment.InvalidContentType" or "WebhookEndpoint.InvalidUrl"
                or "WidgetConfig.InvalidColor" or "WidgetConfig.InvalidPosition"
                or "Site.InvalidName" or "Site.InvalidOrigin" or "ChannelCredential.InvalidToken"
                or "OperatorInvite.InvalidRole" or "Conversation.SearchInvalidQuery"
                // `23-72`: the identical "the caller named a role that does not exist on this site"
                // shape as `OperatorInvite.InvalidRole` right above - a distinct code because the two
                // write paths (invite generation, role change) are otherwise unrelated
                // (`ConversationErrors.OperatorRoleNotFound`'s own remarks), same status.
                or "Operator.RoleNotFound"
                // `18-02`: a real client mistake (naming the operator who already holds the
                // conversation), not a conflict with anything concurrent - see the error's own remarks.
                or "Conversation.TransferTargetIsCurrentOperator"
                // `18-03`: CannedResponseEndpoints' own PUT - an empty/oversized title or body, or too
                // many responses, is the caller's mistake to fix, not a server failure.
                or "CannedResponse.Invalid"
                // `18-04`: the same "caller's mistake to fix" shape - an empty/oversized note body or
                // tag name.
                or "ConversationNote.Invalid" or "Tag.Invalid"
                // `18-08`: the same "the query itself was malformed" shape as
                // Conversation.SearchInvalidQuery, for the analytics panel's own from/to - `18-10`'s
                // conversion report reuses this exact code for its own identical check
                // (ConversationErrors.AnalyticsInvalidRange's own remarks on why it does not get a
                // second one).
                or "Analytics.InvalidRange"
                // `18-10`: a wire value that did not parse to a real, recordable ConversationOutcome -
                // the caller's own mistake to fix, the same shape as the CannedResponse/Tag/Note
                // validation codes right above it.
                or "Conversation.OutcomeInvalid"
                // `18-14`: the same "the query itself was malformed" shape again, for the
                // module-flow report's own from/to (ConversationErrors.ModuleFlowInvalidRange's own
                // remarks on why it is a distinct code from Analytics.InvalidRange rather than a
                // third reuse).
                or "ModuleFlow.InvalidRange"
                // `14-12`: the same "the query itself was malformed" shape - a channel-kind string that
                // does not parse to a real Domain.ChannelKind member.
                or "ChannelLinkRequest.InvalidKind"
                // `14-14`: the same "caller's mistake to fix" shape as ConversationNote.Invalid/
                // Tag.Invalid above - an empty/oversized contact detail value, or a kind string that
                // does not parse to a real Domain.VisitorContactDetailKind member.
                or "VisitorContactDetail.Invalid" or "VisitorContactDetail.InvalidKind"
                // `25-58`: the same "caller's mistake to fix" shape once more - an assessment string
                // that does not parse to a real, settable Domain.VisitorContactDetailAssessment member,
                // or an attempt to confirm/mark invalid a row whose Kind does not support it at all
                // (ConversationErrors.ContactDetailAssessmentNotApplicable's own remarks).
                or "VisitorContactDetail.InvalidAssessment" or "VisitorContactDetail.AssessmentNotApplicable"
                // `14-15`: the caller's own mistake to fix - an unparsable phone number, or a code that
                // did not match (ConversationErrors.PhoneVerificationWrongCode's own remarks on why the
                // message never names a remaining-attempts count).
                or "PhoneVerification.InvalidNumber" or "PhoneVerification.WrongCode"
                // `22-17`: the owner's own mistake to fix - an ExpiresAt that is not strictly in the
                // future, or reaches further out than EnableModuleForSiteAsOwnerHandler.MaxGrantDuration
                // allows.
                or "Module.GrantExpiryInvalid"
                // `23-66`: found while wiring the platform owner's own quantity-grant route - a
                // malformed ModuleKey or a negative Quantity, the caller's own mistake to fix. This
                // code has existed since `20-07`/`EnableModuleForSiteHandler` but had never reached an
                // HTTP response before (every prior caller either passed a valid key by construction in
                // its own tests, or checked the Error value directly at the Application layer without
                // ever going through ToProblem) - unmapped here fell through to the default 500 below,
                // turning an ordinary bad request into a fault. Fixed here rather than filed separately:
                // it is a one-line addition to an existing switch, not a second promise (rule 15), and
                // it is what this item's own new route needs to answer a negative quantity correctly.
                or "Module.Invalid"
                // `25-43`: the platform owner tried to publish a price for a key code has not
                // registered (Domain.PricedResourceKeys.IsKnown), or a negative amount - both
                // the caller's own mistake to fix, the identical shape Module.Invalid's own
                // remarks state for the analogous case one line up.
                or "Billing.PriceKeyUnknown" or "Billing.PriceInvalid"
                // `25-41`: the caller's own mistake to fix - a requested extra-Administrator count at
                // or below the subscription's own current one, the identical "caller's own mistake"
                // shape Module.Invalid's own remarks state a few lines up. Deliberately mapped here,
                // unlike its sibling Billing.SeatCountUnchanged (and Billing.InvalidSeatCount/
                // Billing.SubscriptionNotFound/Billing.SubscriptionNotActive/Billing.PaymentProviderRefused,
                // all still genuinely unmapped, falling through to the `500` default below) - those are
                // a pre-existing, out-of-scope gap this item found but did not introduce, the same
                // "leave a pre-existing gap exactly as found" precedent this file's own Operator.AdminLimitReached
                // entry already states a few lines down for the analogous seat-limit code; a brand-new
                // code this item is introducing is a different case; leaving it deliberately broken
                // would not be "as found", it would be new.
                or "Billing.AdministratorCountNotAnIncrease"
                // `23-13`: the caller's own mistake to fix - Force was set with no non-blank reason, or
                // one longer than RevokeModuleForSiteAsOwnerHandler.MaxReasonLength allows. The same
                // "decide, don't default" shape Module.GrantExpiryInvalid already gives its own guard.
                or "Module.RevokeReasonRequired"
                // `23-86`: the caller's own mistake to fix - the unconditional-grant flag was set or
                // lifted with no non-blank reason, or one longer than
                // SetUnconditionalModuleGrantAsOwnerHandler.MaxReasonLength allows. The identical
                // "decide, don't default" shape Module.RevokeReasonRequired already gives its own
                // override a few lines up.
                or "Module.QuantityUnconditionalGrantReasonRequired"
                // `24-05`: the caller's own mistake to fix - a purpose string that does not parse to a
                // real Domain.VisitorConsentPurpose member, the same "validate the enum, translate the
                // miss" shape WidgetConfig.InvalidPosition/ChannelLinkRequest.InvalidKind already give
                // their own enums.
                or "Document.InvalidPurpose"
                // `23-68`: the identical "decide, don't default" shape Module.RevokeReasonRequired
                // already gives its own override, restated for the seat-restore override - Force was
                // set with no non-blank reason, or one longer than
                // RestoreOperatorSeatAsOwnerHandler.MaxReasonLength allows.
                or "Operator.SeatRestoreReasonRequired"
                // `24-16`: the caller's own mistake to fix - an empty or over-length document key sent
                // to AddRequiredDocumentHandler/RemoveRequiredDocumentHandler, the identical "brand-new
                // code this item is introducing, so it is mapped here rather than left to fall through
                // to the 500 default below" reasoning Module.Invalid's own remarks give a few lines up
                // for the identical situation (a pre-existing unmapped code is a different, deliberately
                // untouched case; a code this item's own new routes can actually now produce is not).
                or "RequiredDocument.Invalid"
                // `22-08`: the caller's own mistake to fix - a non-positive duration/extension in
                // minutes too large to represent as an instant, or a blank/over-length reason on any
                // of the suspend/extend/lift trio. The identical "brand-new code this item's own new
                // routes can actually produce" reasoning `RequiredDocument.Invalid`'s own remarks give
                // a few lines up.
                or "TenantSuspension.DurationInvalid" or "TenantSuspension.ReasonRequired"
                // `25-73`: the admin's own supplied invite email did not parse - the caller's mistake
                // to fix, the identical "brand-new code this item's own new route can actually produce"
                // shape every other 400 in this group already states for itself.
                or "OperatorInvite.InvalidEmail"
                // `25-76`: the platform owner's own role-permission tool - an empty permission list, or
                // one naming a string that is not a real, known Domain.Permission. The identical
                // "brand-new code this item's own new route can actually produce" reasoning every other
                // 400 in this group already states for itself.
                or "Role.PermissionsRequired" or "Role.PermissionUnknown"
                // `25-77`: the removal direction's own mistake to fix - a blank/missing/over-length
                // reason, required unconditionally on every removal (never conditional on a force flag
                // the way Module.RevokeReasonRequired is).
                or "Role.PermissionRemovalReasonRequired"
                // `23-80`: the caller's own mistake to fix - more attachment ids in one bulk-delete
                // call than BulkDeleteSiteAttachmentsHandler.MaxBatchSize allows. The identical
                // "brand-new code this item's own new route can actually produce" reasoning every
                // other 400 in this group already states for itself.
                or "Attachment.BulkDeleteTooMany"
                // `25-83`: the platform owner's own exemption toggle - a blank or over-length reason,
                // the caller's own mistake to fix, the identical "brand-new code this item's own new
                // route can actually produce" reasoning every other 400 in this group already states
                // for itself. Found and fixed by this item's own real-HTTP test
                // (`OwnerDownloadBlockExemptionEndpointTests.OwnerToken_WithNoReason_IsRefused_AndGrantsNothing`) -
                // the same pre-existing unmapped-code gap `Attachment.DownloadBlocked`'s own remarks
                // describe a few lines up, for the write side rather than the read side.
                or "Site.DownloadBlockExemptionReasonRequired"
                // `25-84`: the same two shapes one item later - the owner's own billing-mode toggle
                // with a blank or over-length reason, and a tenant asking to pay for an overage that
                // does not exist. Mapped in the change that introduces them, rather than discovered
                // later as a bare `500` the way `25-83`'s own first defect was.
                or "Site.DownloadOverageBillingModeReasonRequired"
                or "Attachment.DownloadOverageNothingToPay"
                // `25-98`: the rest of this item's own audit findings that belong in this exact
                // "caller's own mistake to fix" group - each reached a real `ToProblem` call and had
                // never been given a line here, all falling through to the `500` default below.
                //
                // `Acceptance.Invalid`/`Document.Invalid` - AcceptanceErrors.Invalid/
                // PublishedDocumentErrors.Invalid both wrap a domain validation exception's own message,
                // the identical "validate the value, translate the throw at the Application boundary"
                // split every other `*.Invalid` code in this group already uses.
                or "Acceptance.Invalid" or "Document.Invalid"
                // `AssignmentPenalty.Invalid`/`ContactVisibility.InvalidRung`/`OfflineAutoReply.Invalid`/
                // `WidgetConfig.InvalidAutoOpenDelay`/`WidgetConfig.InvalidAutoOpenGreetingText`/
                // `WidgetConfig.InvalidLocale`/`WidgetConfig.InvalidNoticeText`/
                // `WidgetConfig.InvalidNoticeUrl` - the identical "validate the value or enum, translate
                // the throw at the Application boundary" split `WidgetConfig.InvalidColor`/
                // `WidgetConfig.InvalidPosition` already draw a few lines up, restated for each field
                // ConversationErrors' own remarks name for it.
                or "AssignmentPenalty.Invalid" or "ContactVisibility.InvalidRung" or "OfflineAutoReply.Invalid"
                or "WidgetConfig.InvalidAutoOpenDelay" or "WidgetConfig.InvalidAutoOpenGreetingText"
                or "WidgetConfig.InvalidLocale" or "WidgetConfig.InvalidNoticeText" or "WidgetConfig.InvalidNoticeUrl"
                // `Billing.InvalidSeatCount` - "the requested seat count falls outside
                // SubscriptionTierBands.MinSeats-MaxSeats ... never a purchasable band"
                // (ConversationErrors.BillingInvalidSeatCount's own remarks) - the caller's own mistake
                // to fix, not a conflict with anything concurrent.
                or "Billing.InvalidSeatCount"
                // `Billing.SeatCountUnchanged` - the identical shape `Conversation.TransferTargetIsCurrentOperator`
                // already gives a few lines up: "a real client mistake (naming the state that already
                // holds)", not a conflict with anything concurrent - the caller asked to change to what
                // is already true, so there is nothing to retry and nothing else to act on first.
                or "Billing.SeatCountUnchanged"
                // `Module.TriggerWordReserved` - "collides with Chat's own closed, product-level command
                // vocabulary ... refused regardless of what any other module has registered"
                // (ConversationErrors.ModuleTriggerWordReserved's own remarks) - the same "caller named a
                // value outside the allowed set" shape `Role.PermissionUnknown` already gives a few lines
                // up, not a conflict with another module's own registration (that shape is
                // `Module.TriggerWordAlreadyRegistered`, mapped separately below, in the 409 group).
                or "Module.TriggerWordReserved"
                // `ModuleTaskChannelPriority.DuplicateEntry` - "the same channel identity named twice in
                // one submitted priority order ... a caller submitting a genuine duplicate has almost
                // certainly made a mistake" (ConversationErrors.ModuleTaskChannelPriorityDuplicateEntry's
                // own remarks) - a malformed request body, not a conflict with anything concurrent.
                or "ModuleTaskChannelPriority.DuplicateEntry" => StatusCodes.Status400BadRequest,
            "Conversation.InvalidState" or "Attachment.VerificationFailed" or "Attachment.NotReady"
                or "Conversation.ConcurrencyConflict" or "Site.AlreadyRegistered"
                or "ChannelCredential.AlreadyConnected" or "OperatorInvite.AlreadyRedeemed"
                or "OperatorInvite.AlreadyOperatorOnSite"
                // `18-02`: both genuinely 409-shaped - "retry the request", not "fix what you sent".
                // TransferTargetAtCapacity is not 402 like OperatorInviteSeatLimitReached: there is no
                // purchase that adds room to one specific operator right now (ConversationErrors'
                // own remarks on why).
                or "Conversation.TransferTargetAtCapacity" or "Conversation.TransferContended"
                // `23-04`: the identical "retry the request" shape as Conversation.TransferContended,
                // for a deliberate take's own transaction losing every attempt against write
                // contention - ConversationErrors.ClaimContended's own remarks.
                or "Conversation.ClaimContended"
                // `18-04`: a real conflict with existing data (a duplicate name), not a malformed
                // request - ConversationErrors.TagAlreadyExists's own remarks.
                or "Tag.AlreadyExists"
                // `14-15`: a genuine race between two concurrent confirmations of the same pending
                // verification - ConversationErrors.PhoneVerificationAlreadyConsumed's own remarks.
                or "PhoneVerification.AlreadyConsumed"
                // `14-15`/`adr/0079` decision 3: the identical "refused, not merged" conflict for a
                // phone number already verified under a different visitor - ConversationErrors.
                // PhoneVerificationAlreadyLinkedToAnotherVisitor's own remarks.
                or "PhoneVerification.AlreadyLinkedToAnotherVisitor"
                // `23-26`: a real conflict with the site's own current state (removing this operator
                // would leave nobody who can manage operators), not a malformed request - the same
                // "retry makes no sense, the remedy is a different action first" shape every other code
                // in this group already gives for its own conflict.
                or "Operator.IsLastManager"
                // `23-68`: pre-existing since `13-03` (the same gap `23-72`'s own remarks a few lines up
                // name for `Operator.NotFound`, and left unmapped there deliberately, out of that
                // item's own scope) but genuinely reachable from this item's own new route: naming an
                // already-removed operator on the platform owner's own restore-seat call falls through
                // to this switch's `500` default without this line. A real conflict with the row's own
                // current state (it is permanently gone, `Operator.Remove`'s own remarks), not a
                // malformed request - the same "retry makes no sense" shape `Operator.IsLastManager`
                // right above already gives its own conflict. Fixed here rather than filed separately,
                // the identical `Module.Invalid`/`Operator.NotFound` precedent: this item's own new
                // route needs it to answer correctly.
                or "Operator.AlreadyRemoved"
                // `23-13`: a real conflict with the row's own current state (it is a tenant's own
                // self-service purchase, not a grant), resolved by an explicit second statement of
                // intent rather than by fixing the request body - the same shape
                // ChannelCredential.AlreadyConnected already gives its own conflict.
                or "Module.RevokePurchaseRequiresForce"
                // `23-68`: the identical "a real conflict, resolved by an explicit second statement of
                // intent" shape Module.RevokePurchaseRequiresForce already gives its own override,
                // restated for the seat-restore override - restoring this seat would put the site over
                // its own seat limit, and the caller did not set Force.
                or "Operator.SeatRestoreExceedsLimitRequiresForce"
                // `24-10`: a real conflict with the conversation's own current block state, not a
                // malformed request - the same "the remedy is a different action first" shape
                // Tag.AlreadyExists/ChannelCredential.AlreadyConnected already give their own conflicts.
                or "Conversation.AlreadyBlocked" or "Conversation.NotBlocked"
                // `23-78`: the identical shape right above, restated for a different current-state
                // pair on the same table - a grant/revoke request against a conversation already in
                // the state being asked for.
                or "Conversation.AttachmentUploadAlreadyGranted" or "Conversation.AttachmentUploadNotGranted"
                // `24-05`: a real conflict with this specific request's own preconditions (this site
                // requires a recorded consent, and this visitor has none yet), resolved by an explicit
                // second act - recording the consent - rather than by fixing the request body, the
                // identical shape ChannelCredential.AlreadyConnected/Module.RevokePurchaseRequiresForce
                // already give their own conflicts (ConversationErrors.VisitorContactDetailConsentRequired's
                // own remarks).
                or "VisitorContactDetail.ConsentRequired"
                // `23-88`: the identical "a real conflict, resolved by an explicit second act - not by
                // fixing this request's own body" shape the whole block above already establishes. The
                // owner confirmed a quantity grant against an impact-preview answer that has since
                // changed, gone stale, or never existed - GrantModuleQuantityAsOwnerHandler's own
                // remarks for exactly which. The remedy is always the same second act: ask again, look
                // at the fresh answer, confirm again - never a different request body.
                or "Module.QuantityImpactStale"
                // `25-43`: two platform-owner publishes for the same price key raced
                // repeatedly - genuinely conflict-shaped, not the caller's mistake to fix and
                // not a transient dependency failure, the identical "retry the exact same
                // request" reasoning Document.PublishConflict's own remarks give for the
                // identical shape.
                or "Billing.PricePublishConflict"
                // `22-08`: a real conflict with the account's own current suspension state - suspend a
                // site that is already suspended, or extend/lift one that is not - resolved by a
                // different action (extend, or nothing), never by fixing the request body, the
                // identical shape `Operator.IsLastManager` right above already gives its own conflict.
                or "TenantSuspension.AlreadySuspended" or "TenantSuspension.NotSuspended"
                // `25-73`: withdrawn by the inviting site before anybody redeemed it - a real conflict
                // with the row's own current state, resolved by nothing the caller can do (a fresh
                // invite from the admin, not a retry), the identical shape `OperatorInvite.AlreadyRedeemed`
                // right above already gives its own sibling conflict.
                or "OperatorInvite.Revoked"
                // `25-98`: the rest of this item's own audit findings that belong in this exact "real
                // conflict with the row's own current state, resolved by a different act, not a retry
                // and not fixing the request body" group - each reached a real `ToProblem` call and had
                // never been given a line here.
                //
                // `AiAddOn.AgreementNotAccepted`/`AiAddOn.BasisNotDeclared` - two of the add-on's own
                // three distinct missing-fact refusals (AiAddOnErrors' own remarks on why there are
                // three, not one `AiAddOn.NotReady`) - a real precondition not yet met, resolved by a
                // separate act (accept the agreement; declare the basis) before enabling, the identical
                // shape `VisitorContactDetail.ConsentRequired` already gives a few lines up.
                or "AiAddOn.AgreementNotAccepted" or "AiAddOn.BasisNotDeclared"
                // `AiAddOn.AgreementVersionStale` - "the version the caller says they read is not the
                // one currently published" (AiAddOnErrors' own remarks) - the identical "confirmed
                // against a stale answer; ask again, look at the fresh one, confirm again" shape
                // `Module.QuantityImpactStale` already gives a few lines up.
                or "AiAddOn.AgreementVersionStale"
                // `Billing.SubscriptionNotActive` - "the subscription named is not currently active
                // (Pending, already Failed/Lapsed) - there is nothing for a cancel or a seat change to
                // act on" (ConversationErrors.BillingSubscriptionNotActive's own remarks) - a real
                // conflict with the row's own current state, the identical shape
                // `TenantSuspension.NotSuspended` already gives a few lines up.
                or "Billing.SubscriptionNotActive"
                // `Document.PublishConflict` - PublishedDocumentErrors.PublishConflict's own remarks
                // name it "`409`, not the caller's mistake to fix and not a transient dependency failure
                // either" in as many words.
                or "Document.PublishConflict"
                // `Module.TriggerWordAlreadyRegistered` - "a trigger word registered here already opens
                // a different module" (ConversationErrors.ModuleTriggerWordAlreadyRegistered's own
                // remarks) - a real conflict with another module's own existing registration, the
                // identical shape `Tag.AlreadyExists` already gives a few lines up (not the caller
                // naming an off-limits value outright - that shape is `Module.TriggerWordReserved`,
                // mapped separately above, in the 400 group).
                or "Module.TriggerWordAlreadyRegistered"
                // `ModuleTaskChannelPriority.NoActiveTask` - "there is no Conversation.ActiveModuleTask
                // to scope a priority list to ... nothing for this call to attach to"
                // (ConversationErrors.ModuleTaskChannelPriorityNoActiveTask's own remarks) - the
                // identical "nothing to act on" shape `TenantSuspension.NotSuspended` already gives a
                // few lines up.
                or "ModuleTaskChannelPriority.NoActiveTask"
                // `Visitor.NotRestricted` - ConversationErrors.VisitorNotRestricted's own remarks name it
                // "the same `409`-shaped ... group `ConversationNotBlocked` already establishes, restated
                // for a visitor" in as many words.
                or "Visitor.NotRestricted" => StatusCodes.Status409Conflict,
            // `13-01`'s own reasoned choice: a real invite that has timed out is "Gone", not "Not
            // Found" - a caller should ask for a fresh one, not retry the same lookup more carefully.
            // `14-15`: the identical shape for an expired verification code - ConversationErrors.
            // PhoneVerificationExpired's own remarks.
            // `23-80`: the identical shape for a deliberately-deleted attachment - ConversationErrors.
            // AttachmentRemoved's own remarks on why this is a distinct code from Attachment.NotReady
            // (400-shaped, retryable) rather than folded into it.
            "OperatorInvite.Expired" or "PhoneVerification.Expired" or "Attachment.Removed" => StatusCodes.Status410Gone,
            // `13-01`'s own reasoned choice: `402 Payment Required`, not a generic `409` - the actual
            // remedy for a site at its seat limit is "upgrade", not "retry", which `402` signals
            // honestly and `409` does not (ConversationErrors.OperatorInviteSeatLimitReached's own
            // remarks).
            // `25-25`: the Administrator-seat counterpart to OperatorInvite.SeatLimitReached, same
            // group and same reasoning - ConversationErrors.OperatorInviteAdminLimitReached's own
            // remarks. Operator.AdminLimitReached joins it here for the identical reason
            // Operator.SeatLimitReached would if it had ever been mapped - see this file's own remarks
            // a few lines up (`23-72`) on that pre-existing, out-of-scope gap, left exactly as found.
            "OperatorInvite.SeatLimitReached" or "OperatorInvite.AdminLimitReached" or "Operator.AdminLimitReached"
                // `23-85`/`adr/0151`: the identical reasoning, applied to a channel entitlement rather
                // than a seat - the actual remedy for "this account has no channel entitlement" is
                // "buy the option," not "retry" (a generic `403`, the group above, would say "you may
                // never do this," which is false the moment the account pays) and not "fix the
                // request" (`400`, ConversationErrors.ChannelInvalidToken's own group - the token the
                // caller sent may be perfectly valid; the account just has not bought the right to use
                // it).
                or "ChannelCredential.NotEntitled"
                // `25-98`: three more codes in this exact "the actual remedy is to pay, not retry and
                // not fix the request" group - each reached a real `ToProblem` call and had never been
                // given a line here.
                //
                // `AiAddOn.NotPurchased` - "the site has no effective quantity of the add-on module -
                // nothing to enable" (AiAddOnErrors' own remarks) - the identical entitlement shape
                // `ChannelCredential.NotEntitled` right above already gives for a different resource.
                or "AiAddOn.NotPurchased"
                // `Billing.PaymentProviderRefused` - "ЮKassa answered but refused the payment-creation
                // request ... a terminal, provider-confirmed refusal a retry of the identical request
                // would not fix" (ConversationErrors.BillingPaymentProviderRefused's own remarks,
                // explicitly distinguishing this from the unhandled-transient-failure `5xx` case the
                // same handler lets propagate) - the actual remedy is a different payment method, the
                // same "this is a payment problem, not a malformed request" reasoning `402` exists for.
                or "Billing.PaymentProviderRefused"
                // `Operator.SeatLimitReached` - ConversationErrors.OperatorSeatLimitReached's own
                // remarks state the status in as many words: "`402 Payment Required`, the identical
                // reasoning `OperatorInviteSeatLimitReached` already gives for the same underlying
                // constraint on a different write path" - this is `23-72`'s own second pre-existing gap,
                // named and deliberately left as found at the time (see this file's own `Operator.NotFound`
                // remarks above); closed now that this item's own audit needs a real reason for every
                // absence rather than an inherited one.
                or "Operator.SeatLimitReached" => StatusCodes.Status402PaymentRequired,
            // `ago-root#352`: a deployment that has not turned demo tenants on genuinely lacks this
            // capability - not "there is nothing at this path" (`404`, explicitly rejected by
            // MintDemoTenantHandler's own remarks: "not a 404 dressed as a feature flag") and not "an
            // upstream dependency is failing right now" (`503`, the group below, where demo.capacity_reached
            // and demo.identity_rejected land). `501 Not Implemented` is the status RFC 7231 reserves for
            // exactly this shape - "the server does not support the functionality required to fulfil the
            // request" - and it is a static property of this deployment's own configuration, not a
            // transient condition any `Retry-After` could ever shorten.
            "demo.disabled" => StatusCodes.Status501NotImplemented,
            // `19-01`: its own distinct rate-limit code, same 429 group - ConversationErrors.ReplyDraftRateLimited's
            // own remarks.
            // `14-15`: its own distinct rate-limit code, same 429 group - ConversationErrors.
            // PhoneVerificationRateLimited's own remarks. LockedOut shares the group for the identical
            // "a fresh attempt, not a permission, is the remedy" reasoning
            // (ConversationErrors.PhoneVerificationLockedOut's own remarks), even though it carries no
            // Retry-After.
            // `ago-root#347`: `demo.rate_limited` belongs in this same group and was simply missing from
            // it - every other rate-limit code above was already mapped correctly, so exceeding the
            // per-IP demo mint limit was the one refusal in this switch that fell through to the `_ =>
            // 500` default below. DemoEndpoints.HandleMintAsync adds its own Retry-After header before
            // reaching this method, by reading the number back out of the error's own message rather
            // than widening Error itself (Ago.Platform.Kernel is out of scope for an ago-chat-only fix)
            // - DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds's own remarks.
            // `ago-root#353`: the five codes right below - Message/Site/Export/ReplyDraft/PhoneVerification.RateLimited
            // - carried no Retry-After at all until this item, this method's own former comment said so
            // in as many words. They do not reuse demo.rate_limited's message round trip (closed for
            // five more call sites, ago-root#353's own item file) - each endpoint computes a conservative
            // wait from configuration it already holds (RateLimitRetryAfter.Conservative) and passes it
            // into this method's own `retryAfter` parameter below, which is where the header is actually
            // set. PhoneVerification.LockedOut still carries none, deliberately - see its own remarks two
            // lines up.
            // `24-11`: PersonExport.RateLimited joins the same group - ConversationErrors.PersonExportRateLimited's
            // own remarks on why it is a distinct code from Export.RateLimited rather than a reuse.
            // `24-05`: Consent.RateLimited joins the same group - ConversationErrors.ConsentRateLimited's
            // own remarks on why it is a distinct code from Message.RateLimited rather than a reuse.
            // `25-73`: the fifth-invites-today bucket joins the same group - OperatorInviteEndpoints'
            // own HandleCreateAsync computes its own conservative Retry-After from configuration it
            // already holds, the identical `RateLimitRetryAfter.Conservative` shape every other code in
            // this group already uses.
            "Message.RateLimited" or "Site.RateLimited" or "Export.RateLimited" or "PersonExport.RateLimited"
                or "ReplyDraft.RateLimited" or "Consent.RateLimited"
                or "PhoneVerification.RateLimited" or "PhoneVerification.LockedOut" or "demo.rate_limited"
                or "OperatorInvite.RateLimited"
                => StatusCodes.Status429TooManyRequests,
            // `14-08`: this deployment, not the caller, is not ready - ConversationErrors.ChannelNotAvailable's
            // own remarks. `19-01`: ReplyDraft.Unavailable is the identical shape - the LLM provider is
            // unreachable, not anything the caller did wrong.
            // `ago-root#352`: demo.capacity_reached joins this group for a related but distinct reason -
            // not "the deployment isn't ready" but "the deployment is at a real ceiling that does not
            // refill on a clock the way a rate limit does" (DemoTenantErrors.CapacityReached's own
            // remarks: "each one expires on its own"), so `429`'s implied "the same request works again
            // shortly, on a schedule" is the wrong promise. `503`'s "the server cannot handle this right
            // now" is the honest one - a live tenant expiring is what frees the room, not the passage of
            // a fixed window.
            // `ago-root#352`: demo.identity_rejected joins for the same "deployment-side dependency, not
            // caller-side mistake" reasoning as ChannelCredential.NotAvailable/ReplyDraft.Unavailable above
            // - KeycloakDemoIdentityProvisioner.CreateAsync returns it whenever Keycloak itself refuses or
            // answers unexpectedly (a `409`/`400` from a randomly generated username colliding, or a `201`
            // with no `Location` header), never because of anything the anonymous demo caller supplied.
            // Not `409`: the caller never named an identifier of their own to conflict with, so there is
            // nothing for *them* to change before retrying - a fresh mint attempt generates a brand new
            // random username server-side, which is exactly `503`'s "try again" and not `409`'s "you
            // conflicted with a specific resource, send a different one".
            "ChannelCredential.NotAvailable" or "ReplyDraft.Unavailable" or "demo.capacity_reached"
                or "demo.identity_rejected"
                // `24-03`: the identical "a dependency of this request is missing, not anything the
                // caller supplied being wrong" shape - a required document was declared but never
                // published yet (ConversationErrors.SiteAgreementUnavailable's own remarks).
                or "Site.AgreementUnavailable"
                // `22-11`: the module deployment refused the provisioning call or could not be
                // reached - a dependency of this request failing, not anything the caller supplied
                // being wrong, the identical reasoning ChannelCredential.NotAvailable/ReplyDraft.Unavailable's
                // own comment gives for its group.
                or "Module.RegistrationFailed"
                // `23-65`/`adr/0150`: the platform owner's own grant/revoke no longer takes the
                // provisioning secret from the caller - a deployment that has not configured it yet is
                // the identical "a dependency of this request is missing, not anything the caller
                // supplied being wrong" shape, not a caller error.
                or "Module.ProvisioningNotConfigured"
                // `23-92`/`adr/0154`: the identical shape, for the module's own entry point - a
                // deployment that has declared no `ModuleEntryPoints:<key>` value for the module the
                // platform owner named is a dependency of this request missing, not anything the caller
                // supplied being wrong (there is no longer an `EntryPoint` field the caller could get
                // wrong at all).
                or "Module.EntryPointNotConfigured"
                // `24-05`: the identical "a dependency of this request is missing" shape as
                // Site.AgreementUnavailable right above - a site turned on RequireContactConsent (or a
                // visitor is trying to accept) before its own consent document was ever published under
                // this purpose's key (PublishedDocumentErrors.ConsentDocumentUnavailable's own remarks).
                or "Document.ConsentDocumentUnavailable"
                // `25-43`: a real charge site (checkout, renewal, a prorated seat-count
                // upgrade) tried to read the currently-effective price for a key nothing has
                // ever published a version under - `25-43`'s own second decision states this
                // is the ordinary "built, not yet for sale" state, and every charge site must
                // refuse cleanly rather than crash or charge zero (PriceCatalogErrors.PriceNotConfigured's
                // own remarks) - the identical "a dependency of this request is missing, not
                // anything the caller supplied being wrong" shape this whole block already uses.
                or "Billing.PriceNotConfigured"
                // `25-98`: `AiAddOn.AgreementNotPublished` - AiAddOnErrors' own remarks call it "a
                // deployment fault, surfaced rather than swallowed": no published agreement exists yet,
                // so nothing can be accepted and nothing can be enabled - the identical "a dependency of
                // this request is missing, not anything the caller supplied being wrong" shape this
                // whole group already uses. Reached a real `ToProblem` call (`AiAddOnEndpoints`) and had
                // never been given a line here.
                or "AiAddOn.AgreementNotPublished" => StatusCodes.Status503ServiceUnavailable,
            // `ago-root#352`: demo.unavailable is deliberately left here rather than given its own status.
            // MintDemoTenantHandler returns it only after ISiteRegistrationRepository.TryRegisterAsync's
            // five-row insert hits its own unique-index violation - a race that port's own remarks call
            // "effectively unreachable in ordinary operation" now that every mint generates a fresh
            // siteId. Unlike its three siblings above, this is not a deliberate business refusal wearing
            // the wrong number; it is an unexpected database conflict reached after a Keycloak identity
            // was already created and then compensated away - the "possibly genuinely 500" case the ticket
            // itself flagged, and 500 is the honest status for "something we did not expect happened while
            // trying to do this."
            "demo.unavailable" => StatusCodes.Status500InternalServerError,
            // `25-98`: this item's own audit read every `*Errors`-shaped factory method in `ago-chat`
            // (165 codes total, cross-checked against this switch and each one's own callers) rather
            // than trust the handful this file's own comments already named as debt. Every code found
            // unmapped either got a line above, in this change, or belongs on this list instead -
            // deliberately absent for a real, checked reason, not by omission:
            //
            // `Message.InvalidBody`/`Message.InvalidContent`/`Message.Unavailable`/
            // `Conversation.CreateRateLimited`/`TeamChat.Forbidden`/`TeamChat.InvalidBody`/
            // `TeamChat.NotFound` - never reach this method at all. `VisitorHub`/`OperatorHub` translate
            // every `Result` failure through `HubException(error.Message)` directly (see each hub's own
            // send/start-conversation/team-message handlers) - SignalR has no `IResult`/`ToProblem`
            // pipeline to run through, the same "free text, not RFC 7807" split this file's own
            // `RetryAfter` doc comment already draws for the one code (`*.RateLimited`) that crosses
            // both. A line for any of these seven here would be dead code: nothing in this codebase ever
            // calls `ToProblem` with one. Confirmed by reading every caller of each factory method, not
            // assumed from the naming - `StartConversationHandler`/`SendVisitorMessageHandler`/
            // `SendOperatorMessageHandler`/`RemoveTeamMessageHandler` and their siblings have no `Api`
            // caller outside `Hubs/`.
            //
            // `TenantSuspension.SessionRefused` - has no caller anywhere in this codebase, not even a
            // test. Its own doc comment claims `Api.Auth.AuthEndpoints.HandleVisitorSessionAsync`'s own
            // refusal, but that method refuses a suspended tenant by hand-building `Results.Problem`
            // directly (`title`/`type: "tenant-suspended"`/`403`), the same pre-`Result<T>`/`Error`
            // pattern this file's own top-of-file remarks already name `AuthEndpoints` for. This factory
            // method was written for a wiring that was never made, or was made and then replaced by the
            // hand-rolled version without the dead code being noticed - either way, mapping it here would
            // not make it reachable, and a status chosen for code nothing ever constructs cannot be
            // "wrong" or "right" in any way a test could show biting. Left unmapped, not mapped to a
            // guess; a real follow-up (either wire `AuthEndpoints` onto this vocabulary, or delete the
            // dead factory method) is outside this item's own one-thing scope (rule 15) and is named in
            // this item's own report rather than folded in here.
            _ => StatusCodes.Status500InternalServerError,
        };

        // `ago-root#353`: the one place every *.RateLimited code's Retry-After header gets set, driven
        // by data the caller attached rather than five near-identical blocks at each call site
        // (AttachmentEndpoints, SitesEndpoints twice, ReplyDraftEndpoints, PhoneVerificationEndpoints).
        // demo.rate_limited (`ago-root#347`) still sets its own header before reaching here, via the
        // message round trip DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds documents - that one
        // predates this item and is out of its scope to touch, so DemoEndpoints keeps its own path.
        //
        // RFC 9110 SS10.2.3: delta-seconds, a non-negative integer. Ceiling, and never below 1 -
        // VisitorSessionRenewalTests treats a `0` as a bug, not a fast retry (ago-widget hammers the
        // endpoint immediately on `0` rather than backing off), so a sub-second wait must still read
        // as "wait a second," not "retry now."
        if (retryAfter is { } wait)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return Results.Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode,
            // A bare slug, not a resolvable URL - matches AuthEndpoints' own `type` values
            // ("rate-limited", "site-not-found"), not a dereferenceable RFC 7807 type URI.
            type: error.Code,
            extensions: new Dictionary<string, object?> { ["traceId"] = httpContext.TraceIdentifier });
    }
}
