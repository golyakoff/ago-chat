namespace Ago.Chat.Domain;

/// <summary>
/// `24-12`: which boundary-crossing read (or owner write) an <c>access_records</c> row is evidence
/// of - domain vocabulary, the same placement reasoning <see cref="ErasureScope"/>/<see cref="ExportStatus"/>
/// already give for their own enums: both `Ago.Chat.Application` (the handlers that mint a row) and
/// `Ago.Chat.Api` (the owner endpoints that mint one directly - see <see cref="AccessRecordActorKind"/>'s
/// own remarks for why those two surfaces write from different layers) need to agree on this word.
///
/// <para><b>Deliberately just the defensible set the backlog item names, not "one member per
/// permission-gated endpoint".</b> `24-12`'s own Scope: "recording everything is a second copy of the
/// traffic and a personal-data store in its own right" - the members below are the boundary-crossing
/// reads that scope names, and nothing else. `CustomerReadInCalendar` is deliberately absent: it is a
/// read in a different repository (`ago-calendar`), on a different database, and this enum cannot
/// reach across that boundary - see this item's own report for why that surface stays a named gap
/// rather than an invented member nothing ever sets.</para>
/// </summary>
public enum AccessRecordKind
{
    /// <summary>`18-07`'s own boundary-crossing read: an operator opens a past, `Closed` conversation
    /// belonging to a visitor they are not currently assigned to on *that* conversation, proven
    /// instead by a live assignment with the *same* visitor elsewhere
    /// (<c>GetVisitorHistoryHandler.HandleHistoricalConversationAsOperatorAsync</c>'s own remarks:
    /// "the first case in this codebase where a message becomes visible to an operator who was never
    /// a party to the conversation that contains it"). The list of prior-conversation summaries
    /// (<c>HandleAsOperatorAsync</c>) is deliberately not its own member - see that handler's own
    /// remarks in this change for why only opening one is recorded.</summary>
    CrossConversationHistoryRead,

    /// <summary>`12-02`/`23-14`: the platform owner's cross-tenant overview - every tenant's own
    /// business-identity data (`personal-data.md`'s own classification of `sites.name`) in one read,
    /// reached by nobody who is a party to any of those tenants. <c>SiteId</c> is <see langword="null"/>
    /// on this row's own kind - the read spans every tenant, not one, so there is no single site to
    /// name (see <see cref="Ago.Chat.Application.Abstractions.AccessRecordToWrite"/>'s own remarks).</summary>
    OwnerSiteList,

    /// <summary>`23-14`: the platform owner's per-tenant detail read - the read-side sibling of
    /// <see cref="OwnerSiteList"/>, this time scoped to one named site.</summary>
    OwnerSiteDetail,

    /// <summary>`22-17`: the platform owner granting a module to a named tenant with no payment.</summary>
    OwnerModuleGrant,

    /// <summary>`22-17`: the platform owner revoking a module grant.</summary>
    OwnerModuleRevoke,

    /// <summary>`14-12`/`adr/0079`: the platform owner's unconditional channel-identity unlink.</summary>
    OwnerChannelIdentityUnlink,
}
