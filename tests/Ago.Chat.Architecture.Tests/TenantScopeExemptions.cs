namespace Ago.Chat.Architecture.Tests;

/// <summary>
/// `17-01`: <b>the deliverable, not the scaffolding.</b> Every use-case entry point in
/// <c>Ago.Chat.Application</c> that does not take a <c>SiteId</c> and gate it through
/// <c>IPermissionChecker</c> is listed here with the reason it is safe anyway. Anything not listed
/// and not gated fails <see cref="TenantScopeTests"/>, so the thirtieth handler cannot quietly omit
/// what the first twenty-nine do - it either checks, or it argues its case here where a reviewer
/// reads it.
///
/// <para>The list is deliberately total in both directions. An entry for something that <em>is</em>
/// gated fails just as loudly as a missing entry, and so does an entry naming a method that no
/// longer exists - an exemption that has quietly stopped applying is exactly the artefact this file
/// exists to prevent, and it would otherwise sit here looking like considered judgment forever.</para>
///
/// <para><b>Read the reasons as claims, not labels.</b> Each one names what actually supplies the
/// scope: a signed token's own claim, a conversation's participant identity, an integration event
/// this system itself published, or - in exactly one case (`12-02`) - a policy at the HTTP edge
/// deliberately deciding a caller may see every tenant. The four categories are set out in
/// `ago-root/docs/architecture/tenant-isolation.md`, which classifies every one of these alongside
/// the gated ones.</para>
/// </summary>
internal static class TenantScopeExemptions
{
    public static readonly IReadOnlyDictionary<string, string> ByEntryPoint = new Dictionary<string, string>
    {
        // ---------------------------------------------------------------------------------------
        // Visitor entry points. A visitor is outside the RBAC model entirely (`adr/0016`), so there
        // is nothing IPermissionChecker could be asked. What replaces it is strictly narrower than a
        // site check: the handler compares the caller's `VisitorId` - taken from the signed visitor
        // token, never from the request - against `conversation.VisitorId`. Being *the* visitor of a
        // conversation implies being on its site; the converse does not hold, which is why the
        // participant comparison is the stronger of the two and the site is not re-checked.
        // ---------------------------------------------------------------------------------------
        ["Ago.Chat.Application.UseCases.ConfirmAttachment.ConfirmAttachmentHandler.HandleAsVisitorAsync"] =
            "Visitor path. Gated by conversation.VisitorId == command.RequestedBy, from the signed visitor token.",
        ["Ago.Chat.Application.UseCases.CreateAttachment.CreateAttachmentHandler.HandleAsVisitorAsync"] =
            "Visitor path. Gated by conversation.VisitorId == command.RequestedBy, from the signed visitor token.",
        ["Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl.GetAttachmentDownloadUrlHandler.HandleAsVisitorAsync"] =
            "Visitor path. Gated by conversation.VisitorId == query.RequestedBy, from the signed visitor token.",
        ["Ago.Chat.Application.UseCases.GetConversationHistory.GetConversationHistoryHandler.HandleAsVisitorAsync"] =
            "Visitor path. Gated by conversation.VisitorId == query.RequestedBy, from the signed visitor token.",
        ["Ago.Chat.Application.UseCases.GetConversationHistory.GetConversationHistoryHandler.HandleDeltaAsVisitorAsync"] =
            "Visitor path. Gated by conversation.VisitorId == query.RequestedBy, from the signed visitor token.",
        ["Ago.Chat.Application.UseCases.SendMessage.SendVisitorMessageHandler.HandleAsync"] =
            "Visitor path. Conversation.AddVisitorMessage rejects an author who is not this conversation's visitor; "
            + "this handler's own pre-checks are rate limiting and body shape, not authorization.",
        ["Ago.Chat.Application.UseCases.StartConversation.StartConversationHandler.HandleAsync"] =
            "Visitor path, and the one that mints the pairing every other visitor check relies on: both SiteId and "
            + "VisitorId come from the signed visitor token, so a visitor cannot name a site their token was not "
            + "issued for. There is no prior object to check ownership of - this is where ownership begins.",

        // ---------------------------------------------------------------------------------------
        // Public, pre-authentication surface. These serve a site's *public* configuration - the same
        // values any visitor's browser is handed during the widget handshake - so there is no
        // tenant secret to leak and no principal to check a permission for.
        // ---------------------------------------------------------------------------------------
        ["Ago.Chat.Application.UseCases.CheckCorsOrigin.CheckCorsOriginHandler.HandleAsync"] =
            "Deliberately cross-tenant and deliberately unauthenticated: answers only \"does any site allow this "
            + "origin at all\", which is layer 1 of `5-01`'s CORS design. Never the per-site origin check - "
            + "AuthEndpoints/HubOriginValidator do that once the site is actually resolved.",
        ["Ago.Chat.Application.UseCases.GetSiteByPublicKey.GetSiteConfigByPublicKeyHandler.HandleAsync"] =
            "The widget handshake. Keyed by the site's public key, which api-design.md states is not a secret; "
            + "returns only the config a visitor's browser is given anyway. No caller identity exists yet - this "
            + "is the call that issues one.",
        ["Ago.Chat.Application.UseCases.GetSiteConfigById.GetSiteConfigByIdHandler.HandleAsync"] =
            "Takes a SiteId but is never reachable with a caller-supplied one: its only callers "
            + "(HubOriginValidator, on both hubs) pass the site from the connection's own validated token claim, "
            + "and no route maps it. Returns the identical public config its by-public-key twin serves.",
        ["Ago.Chat.Application.UseCases.RegisterSite.RegisterSiteHandler.HandleAsync"] =
            "Creates the tenant, so there is no site to be scoped to yet. Gated instead by `10-01`'s "
            + "RequireKeycloakIdentity policy plus one-registration-per-Keycloak-subject, enforced by a unique "
            + "index inside the registration transaction.",

        // ---------------------------------------------------------------------------------------
        // Consumer and worker side. No external caller reaches these: the input is an integration
        // event this system published to its own broker, so the site is a fact already established
        // by the write that raised the event, not a claim to be verified.
        // ---------------------------------------------------------------------------------------
        ["Ago.Chat.Application.UseCases.DispatchWebhooksForEvent.DispatchWebhooksForEventHandler.HandleAsync"] =
            "Consumer side (Ago.Chat.Webhooks). SiteId comes off the ConversationAssignedToOperator/"
            + "ConversationEnded envelope this system itself published, and endpoints are then loaded by that same "
            + "site - a tenant's webhook can only ever be sent that tenant's own event.",
        ["Ago.Chat.Application.UseCases.RecordUnread.RecordUnreadMessageHandler.HandleAsync"] =
            "Consumer side (Ago.Chat.Worker), keyed by conversation id from a MessageAccepted envelope. Increments a "
            + "counter on that same conversation and reads nothing back to any caller.",
        ["Ago.Chat.Application.UseCases.ResolveConversationAssignment.ResolveConversationAssignmentTargetsHandler.HandleAsync"] =
            "Consumer side. Fan-out to the two principals the assignment event itself names; no lookup, no caller.",
        ["Ago.Chat.Application.UseCases.ResolveMessageDelivery.ResolveMessageDeliveryTargetsHandler.HandleAsync"] =
            "Consumer side. Fan-out to the conversation's own participants, resolved from the conversation row; "
            + "it acts on behalf of nobody, so routing it through an authorized read path would be a layering "
            + "fiction rather than a check (this handler's own remarks say so).",
        ["Ago.Chat.Application.UseCases.ResolveOperatorIdentity.ResolveOperatorIdentityHandler.HandleAsync"] =
            "The claims transformation's own lookup - it is what *produces* the OperatorId/SiteId claims every "
            + "gated handler then trusts, so it cannot itself depend on them. Keyed by the `sub` of an "
            + "already-signature-validated Keycloak token.",

        // ---------------------------------------------------------------------------------------
        // The one deliberate cross-tenant read in the codebase.
        // ---------------------------------------------------------------------------------------
        ["Ago.Chat.Application.UseCases.ListSitesForOwner.ListSitesForOwnerHandler.HandleAsync"] =
            "`12-02`'s platform-owner overview - the single query in ago-chat that reads across every tenant, and "
            + "the reason this file exists in the shape it does. It carries no SiteId *because* it is cross-tenant, "
            + "so a rule that only looked at SiteId-carrying inputs would never have seen it at all. The whole "
            + "access-control story is `12-01`'s RequirePlatformOwner policy on GET /api/v1/owner/sites: the "
            + "authorizing fact is a Keycloak realm role (adr/0032) and Ago.Chat.Application has no port that sees "
            + "claims, so re-checking here would be a second, weaker copy of the same rule. Read-only; no owner "
            + "write surface exists.",
    };
}
