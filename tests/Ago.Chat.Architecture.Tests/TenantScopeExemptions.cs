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
        ["Ago.Chat.Application.UseCases.ConfirmPhoneVerification.ConfirmPhoneVerificationHandler.HandleAsVisitorAsync"] =
            "`14-15`. Visitor path. Gated by conversation.VisitorId == command.RequestedBy, from the signed visitor "
            + "token - the identical shape ConfirmAttachmentHandler's own entry above uses. The pending "
            + "verification itself is then cross-checked against that same conversation's own VisitorId/SiteId "
            + "(ConfirmPhoneVerificationHandler's own remarks on why this second check exists, unlike `14-12`'s "
            + "inbound-message confirmation branch), so a caller cannot confirm a code issued to a different "
            + "visitor's request even within the same site.",
        ["Ago.Chat.Application.UseCases.CreateAttachment.CreateAttachmentHandler.HandleAsVisitorAsync"] =
            "Visitor path. Gated by conversation.VisitorId == command.RequestedBy, from the signed visitor token.",
        ["Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl.GetAttachmentDownloadUrlHandler.HandleAsVisitorAsync"] =
            "Visitor path. Gated by conversation.VisitorId == query.RequestedBy, from the signed visitor token.",
        ["Ago.Chat.Application.UseCases.GetConversationHistory.GetConversationHistoryHandler.HandleAsVisitorAsync"] =
            "Visitor path. Gated by conversation.VisitorId == query.RequestedBy, from the signed visitor token.",
        ["Ago.Chat.Application.UseCases.GetConversationHistory.GetConversationHistoryHandler.HandleDeltaAsVisitorAsync"] =
            "Visitor path. Gated by conversation.VisitorId == query.RequestedBy, from the signed visitor token.",
        ["Ago.Chat.Application.UseCases.InitiatePhoneVerification.InitiatePhoneVerificationHandler.HandleAsVisitorAsync"] =
            "`14-15`. Visitor path. Gated by conversation.VisitorId == command.RequestedBy, from the signed visitor "
            + "token - the identical shape CreateAttachmentHandler's own entry above uses. No operator-initiated "
            + "twin exists for this item (InitiatePhoneVerificationHandler's own remarks on why), so there is "
            + "nothing else on this entry point to gate.",
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
        ["Ago.Chat.Application.UseCases.MintDemoTenant.MintDemoTenantHandler.HandleAsync"] =
            "`8-07`/`adr/0058`. Creates the tenant, so there is no site to be scoped to yet - the same category "
            + "as RegisterSiteHandler below, and reusing that handler's own bootstrap transaction. The "
            + "difference is that this one is reachable with no principal at all, deliberately: Done-when #1 is "
            + "that a stranger gets credentials without anybody intervening, and any gate defeats it. What "
            + "replaces authentication is two guards authentication would not have provided anyway - a per-IP "
            + "rate limit and a total cap on live demo tenants, both enforced in the handler and both with "
            + "tests. Nothing it creates can reach another tenant's data: it only ever writes a brand-new "
            + "Site/Operator/roles package, never reads or touches an existing site, and the whole package is "
            + "deleted within a day. Off unless DemoTenant:Enabled says otherwise, so a deployment that has "
            + "not opted in has no such endpoint at all.",
        ["Ago.Chat.Application.UseCases.RegisterSite.RegisterSiteHandler.HandleAsync"] =
            "Creates the tenant, so there is no site to be scoped to yet. Gated instead by `10-01`'s "
            + "RequireKeycloakIdentity policy plus one-registration-per-Keycloak-subject, enforced by a unique "
            + "index inside the registration transaction.",
        ["Ago.Chat.Application.UseCases.RedeemOperatorInvite.RedeemOperatorInviteHandler.HandleAsync"] =
            "`13-01`. The redeeming caller has no SiteId claim yet, by definition (gated by "
            + "RequireKeycloakIdentity, the same category and the same reason as RegisterSiteHandler right above) "
            + "- the whole point of this call is to *acquire* one. The site the write actually lands on is never a "
            + "caller-supplied value: OperatorInviteRedemptionRepository looks the invite up by its own code_hash "
            + "and then acts on invite.SiteId, a fact the earlier CreateOperatorInviteHandler call already "
            + "established (gated by SiteManageOperators the ordinary way) - the presented code is what proves the "
            + "caller was actually handed an invite for that site, structurally the same 'ownership already proven "
            + "by construction' shape the visitor entries above use their signed token for.",

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
        ["Ago.Chat.Application.UseCases.SendOfflineAutoReply.SendOfflineAutoReplyHandler.HandleAsync"] =
            "`14-04`, consumer side (Ago.Chat.Worker). SiteId comes off a MessageAccepted envelope this system "
            + "itself published, so it is a fact the triggering write already established, not a claim - the same "
            + "category as RecordUnreadMessageHandler right above. There is also no principal to check a permission "
            + "for: nobody asked for this, a broker delivery did, and the message it writes is authored by the "
            + "system itself (adr/0016 has no representation for that caller, exactly as it has none for a "
            + "visitor). What the site id is actually used for is narrow and self-consistent: reading that same "
            + "site's own configuration, and asking whether that same site has an operator online. Nothing is read "
            + "back to any caller.",
        ["Ago.Chat.Application.UseCases.ResolveConversationAssignment.ResolveConversationAssignmentTargetsHandler.HandleAsync"] =
            "Consumer side. Fan-out to the two principals the assignment event itself names; no lookup, no caller.",
        ["Ago.Chat.Application.UseCases.ResolveMessageDelivery.ResolveMessageDeliveryTargetsHandler.HandleAsync"] =
            "Consumer side. Fan-out to the conversation's own participants, resolved from the conversation row; "
            + "it acts on behalf of nobody, so routing it through an authorized read path would be a layering "
            + "fiction rather than a check (this handler's own remarks say so).",
        ["Ago.Chat.Application.UseCases.ReceiveChannelMessage.ReceiveChannelMessageHandler.HandleAsync"] =
            "`14-01`, adapter side (AGO Inbox). Carries a SiteId that no external caller can influence: a channel "
            + "provider's payload has no way to name a site, so the concrete adapter resolves it from the "
            + "credentials the message arrived on - the site that owns the MAX bot token, or rents the SMS long "
            + "number - before constructing the command. That makes it the same category as the consumer-side "
            + "entries above: the tenant is a fact established by our own configuration, not a claim to verify. "
            + "There is also no principal to check a permission for - an SMS sender is outside the RBAC model "
            + "exactly as a visitor is (adr/0016) - and what replaces it is stronger than a site check: every "
            + "write goes to the Visitor that this site's own ChannelIdentity row resolves to, so a message can "
            + "only ever land in a conversation belonging to the site whose credentials received it.",
        ["Ago.Chat.Application.UseCases.DeliverChannelMessage.DeliverChannelMessageHandler.HandleAsync"] =
            "`14-02`, consumer side (Ago.Chat.Worker). SiteId comes off the same MessageAccepted envelope "
            + "SendOfflineAutoReplyHandler/RecordUnreadMessageHandler already read it from - a fact the triggering "
            + "write established, not a claim to verify. There is also no principal to check a permission for: "
            + "nobody asked for this relay, a broker delivery did. What the site id is used for is narrow: loading "
            + "the conversation it names and asking whether its visitor has a linked ChannelIdentity - the actual "
            + "authorization-shaped question (\"may this message reach that MAX chat\") is answered structurally, "
            + "by IChannelIdentityRepository.FindMostRecentForVisitorAsync only ever returning an identity that "
            + "belongs to this exact conversation's own visitor, never a caller-suppliable one.",
        ["Ago.Chat.Application.UseCases.AutoCloseConversation.AutoCloseConversationHandler.HandleAsync"] =
            "`18-06`, worker side (Ago.Chat.Worker), but keyed by neither a caller nor a broker event - the only "
            + "input is a ConversationId that AutoCloseInactiveConversationsJob's own candidate scan already "
            + "restricted to Assigned conversations past their per-channel-kind inactivity window, a fact the "
            + "scan itself established by reading conversations.state and messages.created_at, not a claim to "
            + "verify. There is also no principal to check a permission for: nobody asked for this close, a "
            + "scheduled sweep did, and CloseConversationHandler's own IPermissionChecker/OperatorId gate "
            + "answers \"may this operator close this conversation\" - a question with no subject when the "
            + "caller is not an operator at all (this handler's own remarks explain why that made a second "
            + "handler the right shape, not a nullable OperatorId branch on the first). What a SiteId check "
            + "would have protected against - reaching another tenant's row - is already ruled out "
            + "structurally: IConversationRepository.GetByIdAsync loads exactly the row named by the "
            + "ConversationId the scan produced, and Conversation.Close() only ever mutates that one aggregate.",
        ["Ago.Chat.Application.UseCases.CategorizeConversation.CategorizeConversationHandler.HandleAsync"] =
            "`19-02`, worker side (Ago.Chat.Worker), the same category as AutoCloseConversationHandler right "
            + "above: the only input is a (ConversationId, SiteId) pair that ConversationCategorizationJob's own "
            + "candidate scan (ConversationCategorizationQuery) already restricted to Closed, still-untagged "
            + "conversations within their lookback window, a fact the scan itself established by reading "
            + "conversations.state/closed_at and the absence of any conversation_tags row, not a claim to verify. "
            + "There is also no principal to check a permission for: nobody asked for this categorization, a "
            + "scheduled sweep did. What the SiteId is actually used for is narrow and matches the scan's own "
            + "pairing: loading exactly that site's own tag vocabulary (ITagRepository.GetAllForSiteAsync) to "
            + "build the candidate list a caller cannot influence, and every write "
            + "(ITagRepository.AddToConversationAsync) lands only on the one ConversationId the scan produced - "
            + "reaching another tenant's row is ruled out the identical structural way "
            + "AutoCloseConversationHandler's own remarks describe for IConversationRepository.GetByIdAsync.",
        ["Ago.Chat.Application.UseCases.ResolveOperatorIdentity.ResolveOperatorIdentityHandler.HandleAsync"] =
            "The claims transformation's own lookup - it is what *produces* the OperatorId/SiteId claims every "
            + "gated handler then trusts, so it cannot itself depend on them. Keyed by the `sub` of an "
            + "already-signature-validated Keycloak token.",
        ["Ago.Chat.Application.UseCases.ListMyTenancies.ListMyTenanciesHandler.HandleAsync"] =
            "`13-07`/`adr/0068`. No single SiteId to scope to *by design* - this is the console switcher's own "
            + "read, \"every Site this identity administers\", so a SiteId parameter would be a lie about what "
            + "the call answers. Gated by `RequireKeycloakIdentity` (RegisterSiteHandler's own policy, for the "
            + "identical reason: an identity with zero or several tenancies cannot satisfy RequireOperatorIdentity "
            + "yet). What actually keeps this from being a cross-tenant leak is narrower than a permission check: "
            + "IOperatorRepository.ListByExternalSubjectIdAsync filters at the query itself on the caller's own "
            + "`sub` (read from the validated token, never from the request - MeEndpoints' own remarks), so the "
            + "row set this handler can ever see is already restricted to that identity's own operator rows before "
            + "a single Site is joined in. Structurally the same category as ResolveOperatorIdentityHandler right "
            + "above - both are `sub`-keyed lookups feeding an identity's own tenancy, not a cross-tenant read the "
            + "way ListSitesForOwnerHandler below genuinely is.",
        ["Ago.Chat.Application.UseCases.GetOwnAnalyticsForOperator.GetOwnAnalyticsForOperatorHandler.HandleAsync"] =
            "`23-18`. Carries a SiteId (unlike ResolveOperatorIdentityHandler/ListMyTenanciesHandler right above, "
            + "which carry none at all) but no IPermissionChecker call, by deliberate design rather than omission: "
            + "the backlog item's own words are \"a grant would be a thing a tenant could withhold - which is the "
            + "failure this item exists to prevent.\" What replaces the permission check is narrower than one, the "
            + "same shape ListMyTenanciesHandler's own remarks describe: GetOwnAnalyticsForOperator.RequestedBy is "
            + "the only identifier this query carries - there is no second, operator-scoping parameter anywhere on "
            + "it for a caller to substitute another operator's id into - and every read this handler issues is "
            + "filtered, after the fact, down to exactly the row matching that same RequestedBy "
            + "(OperatorAnalyticsMerge.ComposeByOperator(...).SingleOrDefault(o => o.OperatorId == "
            + "query.RequestedBy.Value), and the identical filter on the conversion read). SiteId itself is not "
            + "the scope boundary here - RequireOperatorIdentity already fixed the (OperatorId, SiteId) pair "
            + "together from the caller's own validated token before this handler is ever constructed "
            + "(ConversationsEndpoints.HandleGetOwnAnalyticsAsync), so there is no combination of the two this "
            + "caller could supply that names anyone but themselves, on any site but their own. The three "
            + "underlying reads (IOperatorAnalyticsReadStore/IOperatorLoadReportReadStore/IConversionReportReadStore) "
            + "are the identical site-scoped ports GetOperatorAnalyticsForSiteHandler/GetConversionReportForSiteHandler "
            + "call under a real SiteConfigure gate - this handler adds no new query shape, only a narrower filter "
            + "on the same rows, which is why sharing the merge rather than restating it "
            + "(GetOwnAnalyticsForOperatorHandler's own remarks) is what keeps this exemption true rather than "
            + "merely asserted.",

        // ---------------------------------------------------------------------------------------
        // `4-06`. No SiteId at all, and deliberately so: the only input is the caller's own
        // OperatorId, resolved by OperatorHub from the connection's own validated JWT before either
        // method is ever invoked - there is no site-scoped *resource* being acted on to check
        // ownership of, only "record this connection's own presence". A SiteId parameter here would
        // invite exactly the kind of second, unverifiable claim RegisterSiteHandler's own SiteId
        // never is: nothing downstream reads it back, and IOperatorRepository.GetByIdAsync resolves
        // the row by that same id, never by a caller-supplied site.
        // ---------------------------------------------------------------------------------------
        ["Ago.Chat.Application.UseCases.SetOperatorPresence.SetOperatorPresenceHandler.GoOnlineAsync"] =
            "OperatorHub.OnConnectedAsync's own wiring. OperatorId is the caller's own identity from the "
            + "connection's JWT, not a resource named by the caller - there is no \"whose presence\" question a "
            + "site check could answer that GetByIdAsync's own key does not already settle.",
        ["Ago.Chat.Application.UseCases.SetOperatorPresence.SetOperatorPresenceHandler.GoOfflineAsync"] =
            "The mirror image of GoOnlineAsync right above - same reasoning, called from "
            + "OperatorHub.OnDisconnectedAsync only when this was the operator's last live connection.",
        ["Ago.Chat.Application.UseCases.SetOperatorPresence.SetOperatorPresenceHandler.NoteConnectedAsync"] =
            "`23-20`. Same reasoning as GoOnlineAsync above, and the same call site (OperatorHub.OnConnectedAsync) "
            + "- this replaced GoOnlineAsync there so a mere reconnect could stop carrying the authority to cancel "
            + "a deliberate Away (Operator.NoteConnected's own remarks).",
        ["Ago.Chat.Application.UseCases.SetOperatorPresence.SetOperatorPresenceHandler.GoAwayAsync"] =
            "`23-20`. Same reasoning as GoOnlineAsync/GoOfflineAsync above, called from "
            + "OperatorHub.SetAwayAsync(true) - the console's own deliberate \"I'm stepping away\" action.",
        ["Ago.Chat.Application.UseCases.GetOperatorPresence.GetOperatorPresenceHandler.HandleAsync"] =
            "`23-20`. The read half of the three entries right above - same reasoning: OperatorId is the caller's "
            + "own identity from the connection's JWT (OperatorHub.GetMyPresenceAsync), not a resource the caller "
            + "names, so there is no \"whose presence\" question a site check could answer.",

        // ---------------------------------------------------------------------------------------
        // `13-02`. Inbound, HTTP-triggered (not broker-consumed) - the third-party mirror of the
        // consumer/adapter category above, not the broker itself.
        // ---------------------------------------------------------------------------------------
        ["Ago.Chat.Application.UseCases.ProcessYooKassaWebhook.ProcessYooKassaWebhookHandler.HandleAsync"] =
            "`13-02`/`adr/0025`. Carries no SiteId at all, by design: the input is ЮKassa's own payment id, which "
            + "no external caller can choose a site with - IBillingWebhookApplier resolves the one billing_subscriptions "
            + "row that payment id names and acts on *that* row's own SiteId, a fact CreateCheckoutSessionHandler "
            + "already established (gated by SiteConfigure the ordinary way) at checkout-session creation, never a "
            + "value this webhook call supplies itself. Structurally the same category as "
            + "ReceiveChannelMessageHandler above (the site is a fact established by our own prior write, not a "
            + "caller's claim) with an even narrower attack surface: this handler cannot even be reached with an "
            + "unverified payload at all - HandleYooKassaWebhookAsync (the endpoint) rejects a missing/invalid "
            + "`Webhook-Signature` header before this handler is ever constructed, so every payment id this method "
            + "ever sees is one ЮKassa itself signed with a key only this deployment and ЮKassa hold. There is also "
            + "no principal to check a permission for - nobody asked for this write, ЮKassa's own webhook delivery "
            + "did, the same 'no principal' category SendOfflineAutoReplyHandler/DeliverChannelMessageHandler above "
            + "are in for the identical reason.",
        ["Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewalHandler.HandleAsync"] =
            "`13-03`, worker side (Ago.Chat.Worker), the same category as AutoCloseConversationHandler above: the "
            + "only input is a BillingSubscriptionId that SubscriptionRenewalJob's own candidate scan "
            + "(IBillingSubscriptionRepository.ListDueForRenewalAsync) already restricted to rows due for renewal "
            + "or retry by reading billing_subscriptions.status/current_period_end/last_renewal_attempt_at, not a "
            + "claim a caller supplies. The site this handler's own applier (ISubscriptionRenewalApplier) ends up "
            + "writing to is never a value this call carries at all - it comes from subscription.SiteId, a fact "
            + "CreateCheckoutSessionHandler (gated by SiteConfigure) and BillingWebhookApplier (a verified ЮKassa "
            + "webhook) already established when that row was created and first activated, exactly the "
            + "'site is a fact established by our own prior write' category ProcessYooKassaWebhookHandler right "
            + "above is in. There is also no principal to check a permission for: nobody asked for this renewal "
            + "attempt, a scheduled job did.",

        ["Ago.Chat.Application.UseCases.RouteConversationToModule.RouteConversationToModuleHandler.HandleAsync"] =
            "`20-07`, consumer side (Ago.Chat.Worker), the same category as SendOfflineAutoReplyHandler/"
            + "DeliverChannelMessageHandler above. SiteId comes off the same MessageAccepted envelope those two "
            + "handlers already read it from - a fact the triggering write established, not a claim to verify. "
            + "There is also no principal to check a permission for: nobody asked for this routing decision, a "
            + "broker delivery did, and the module task it starts/advances/closes and the message it writes are "
            + "both scoped to the one Conversation the envelope names, exactly as SendOfflineAutoReplyHandler's "
            + "own reply is. What the site id is used for is narrow and read-only: resolving which modules this "
            + "site has enabled (IEnabledModuleReadStore.GetForSiteAsync) - nothing is read back to any caller.",

        ["Ago.Chat.Application.UseCases.HandleLinkIdentityCommand.HandleLinkIdentityCommandHandler.HandleAsync"] =
            "`14-12`, consumer side (Ago.Chat.Worker), the same category as SendOfflineAutoReplyHandler/"
            + "RouteConversationToModuleHandler above. SiteId comes off the same MessageAccepted envelope those two "
            + "handlers already read it from - a fact the triggering write established, not a claim to verify. "
            + "There is also no principal to check a permission for: nobody asked for this, a broker delivery did, "
            + "and both the PendingChannelLinkRequest it creates and the reply message it writes are scoped to the "
            + "one Conversation the envelope names, exactly as SendOfflineAutoReplyHandler's own reply is. What the "
            + "site id is used for is narrow: stamping it onto the new pending-request row so a later confirmation "
            + "can only ever match it against messages arriving on that same site (PendingChannelLinkRequest's own "
            + "cross-site-isolation remarks) - nothing is read back to any caller.",

        // ---------------------------------------------------------------------------------------
        // The two deliberate cross-tenant/owner-only surfaces in the codebase.
        // ---------------------------------------------------------------------------------------
        ["Ago.Chat.Application.UseCases.ListSitesForOwner.ListSitesForOwnerHandler.HandleAsync"] =
            "`12-02`'s platform-owner overview - the single query in ago-chat that reads across every tenant, and "
            + "the reason this file exists in the shape it does. It carries no SiteId *because* it is cross-tenant, "
            + "so a rule that only looked at SiteId-carrying inputs would never have seen it at all. The whole "
            + "access-control story is `12-01`'s RequirePlatformOwner policy on GET /api/v1/owner/sites: the "
            + "authorizing fact is a Keycloak realm role (adr/0032) and Ago.Chat.Application has no port that sees "
            + "claims, so re-checking here would be a second, weaker copy of the same rule. Read-only; no owner "
            + "write surface exists.",
        ["Ago.Chat.Application.UseCases.GetSiteForOwner.GetSiteForOwnerHandler.HandleAsync"] =
            "`23-14`, the per-tenant companion to ListSitesForOwnerHandler right above - a genuinely different "
            + "shape from this shape's other neighbours, which is worth stating precisely: unlike "
            + "ListSitesForOwnerHandler, this one DOES take a SiteId (a query record, not just a command, can carry "
            + "one), which is exactly why it needs an entry here at all rather than being invisible to the rule the "
            + "way its sibling is. The SiteId is chosen by the platform owner, not resolved from a token, the "
            + "identical 'caller names the tenant' shape the owner's three cross-tenant writes already have "
            + "(UnlinkChannelIdentityAsOwnerHandler/EnableModuleForSiteAsOwnerHandler/"
            + "RevokeModuleForSiteAsOwnerHandler above) - the read-side counterpart of that shape rather than a "
            + "fourth write. The whole access-control story is RequirePlatformOwner on "
            + "GET /api/v1/owner/sites/{siteId} (OwnerSitesEndpoints): Ago.Chat.Application still has no port that "
            + "can see a Keycloak realm-role claim, so a permission check here would be a second, weaker copy of a "
            + "rule the policy already decided. Do not confuse this with ListEnabledModulesForSiteHandler (`23-01`), "
            + "which also takes a SiteId but is gated - it carries a RequestedBy its handler checks through "
            + "IPermissionChecker, because that read is a tenant's own operator looking at their own site; this one "
            + "carries no requester at all, because it is the platform owner looking at any site they name. Read-"
            + "only; the SiteId is used only to load one row (IPlatformOverviewReadStore.GetSiteAsync) and that "
            + "row's own modules (IEnabledModuleReadStore.GetAllForSiteAsync) - nothing is written.",
        ["Ago.Chat.Application.UseCases.UnlinkChannelIdentityAsOwner.UnlinkChannelIdentityAsOwnerHandler.HandleAsync"] =
            "`14-12`, the platform owner's own first write surface - see the handler's own remarks for why it is a "
            + "deliberately separate class from UnlinkChannelIdentityHandler rather than a nullable-OperatorId "
            + "branch on it. Unlike ListSitesForOwnerHandler above, this one does take a SiteId, but it is never "
            + "checked through IPermissionChecker: the whole access-control story is the RequirePlatformOwner policy "
            + "on the owner-scoped route that resolves it (the same single-gate shape that entry's own remarks "
            + "describe), and Ago.Chat.Application still has no port that can see a Keycloak realm-role claim, so a "
            + "permission check here would again be a second, weaker copy of a rule the policy already decided. The "
            + "SiteId is used only as a structural cross-check against the loaded ChannelIdentity's own real site - "
            + "refusing a caller who named the wrong site in the URL, never a claim this handler trusts to scope a "
            + "query.",
        ["Ago.Chat.Application.UseCases.EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler.HandleAsync"] =
            "`22-17`, the platform owner's own deliberate cross-tenant write - granting a module to a named tenant "
            + "with no payment (sales trials, and repairing a payment that succeeded without provisioning). The "
            + "identical category as UnlinkChannelIdentityAsOwnerHandler right above: SiteId names the tenant being "
            + "granted the module, not a resource the caller already owns, and the whole access-control story is "
            + "the RequirePlatformOwner policy on OwnerModuleEndpoints - Ago.Chat.Application still has no port that "
            + "can see a Keycloak realm-role claim, so a permission check here would be a second, weaker copy of a "
            + "rule the policy already decided. Unlike UnlinkChannelIdentityAsOwnerHandler, there is no prior row to "
            + "cross-check the SiteId against - granting is what brings the EnabledModule row into existence, the "
            + "same 'no prior object to check ownership of' shape RegisterSiteHandler's own entry describes for "
            + "creating a tenant.",
        ["Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler.HandleAsync"] =
            "`22-17`, the platform owner's own revoke - the mirror of EnableModuleForSiteAsOwnerHandler right above, "
            + "proving a grant can be taken back. Same reasoning: SiteId names the tenant being acted on, not a "
            + "resource the caller already owns, and RequirePlatformOwner on OwnerModuleEndpoints is the entire "
            + "access-control story. The SiteId is used to load the EnabledModule row being revoked - "
            + "modules.GetAsync(siteId, moduleKey) - so a caller cannot revoke a different site's registration by "
            + "naming its module key against the wrong SiteId; the (site, module) pair is the row's own key, the "
            + "same structural protection RevokeModuleForSiteHandler's own operator-gated sibling gets from its "
            + "additional IPermissionChecker call. `23-13` added a force flag and a recorded reason for revoking a "
            + "tenant's own self-service purchase, but neither is a second permission check - RequirePlatformOwner "
            + "on the route remains the entire access-control story unchanged; force/reason gate a business "
            + "decision (was this override meant), not who may call the route at all.",

        // ---------------------------------------------------------------------------------------
        // `24-01`: the acceptance record's own two handlers. Neither carries a SiteId at all - not
        // an omission, a deliberate consequence of what an acceptance is. Recording your own
        // acceptance is not an act on a tenant-scoped resource the way writing a conversation note
        // is; it is closer to "the caller asserts a fact about themselves" - the same self-service
        // shape a login or a token refresh already has, and RecordAcceptance's own remarks make the
        // same argument in the command's own doc comment. Which caller may invoke either handler at
        // all, and under what authentication, is deliberately left to `24-03`/`24-04`/`24-05` - this
        // item builds no host endpoint for either (Scope: "showing anything to anybody" is those
        // items' job), so there is no route yet for a permission policy to sit behind either.
        // ---------------------------------------------------------------------------------------
        ["Ago.Chat.Application.UseCases.RecordAcceptance.RecordAcceptanceHandler.HandleAsync"] =
            "`24-01`. Subject-agnostic by design: the command names its own subject (Tenant/Operator/Visitor, "
            + "AcceptanceSubjectKind) rather than a site, and recording an acceptance is a self-service assertion "
            + "about the caller, not an operation gated on a tenant's own permission set. No host endpoint exists "
            + "yet - 24-03/24-04/24-05 build the real entry points and their own authentication, at which point "
            + "each of those callers is responsible for only ever invoking this with the caller's own subject id.",
        ["Ago.Chat.Application.UseCases.GetAcceptancesForSubject.GetAcceptancesForSubjectHandler.HandleAsync"] =
            "`24-01`. The read-back half of RecordAcceptanceHandler right above, same reasoning: no SiteId, because "
            + "an acceptance's subject is not a site. Deliberately unauthenticated at this layer - this item's own "
            + "Scope excludes showing anything to anybody, so no host endpoint calls this yet; it exists so the "
            + "record-then-read-back round trip is provable in a test.",

        // ---------------------------------------------------------------------------------------
        // `24-02`: the published-document pair. Neither carries a SiteId - a document is not tenant
        // data at all, it is AGO's own (`24-02`'s own "the text is ago-business's and a lawyer's; the
        // mechanism is ours"), so there is no tenant to scope either call to.
        // ---------------------------------------------------------------------------------------
        ["Ago.Chat.Application.UseCases.PublishDocumentVersion.PublishDocumentVersionHandler.HandleAsync"] =
            "`24-02`. No SiteId because a document is not a tenant's resource - the entire access-control story is "
            + "OwnerDocumentEndpoints's own RequirePlatformOwner gate (the same single-gate shape OwnerModuleEndpoints "
            + "already uses), which this handler has no way to see or duplicate: Ago.Chat.Application still has no "
            + "port onto a Keycloak realm-role claim, the identical reasoning EnableModuleForSiteAsOwnerHandler's own "
            + "entry gives.",
        ["Ago.Chat.Application.UseCases.GetDocumentVersion.GetDocumentVersionHandler.HandleAsync"] =
            "`24-02`. Deliberately unauthenticated - the whole point of this handler is the published surface a "
            + "caller with no account (nobody has accepted anything yet) can read, `24-02`'s own Scope: \"somebody "
            + "who has not yet accepted anything has no account to read it from.\" No SiteId for the same reason as "
            + "PublishDocumentVersionHandler right above: a document is not a tenant's resource.",
    };
}
