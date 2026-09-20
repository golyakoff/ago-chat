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

    /// <summary>`23-66`/`adr/0093`/`adr/0150`: the platform owner granting a module's own countable
    /// quantity for a named tenant - the calendar add-on's "N masters" is the first real instance,
    /// carrying no word of that here (the identical opacity <see cref="OwnerModuleGrant"/>'s own
    /// resource already keeps for the module key itself).</summary>
    OwnerModuleQuantityGrant,

    /// <summary>`23-68`: the platform owner restoring a named operator's own seat - the recovery path
    /// for a tenant locked out with no other way back in (`docs/backlog/23-68-*.md`'s own "Found": a
    /// tenant locked itself out and the only way back was a hand-typed database `UPDATE`). Written on
    /// every successful call, whether or not the site's own seat limit was exceeded to do it - the
    /// seat-limit override itself is a second, narrower fact, attested separately by
    /// <see cref="Ago.Chat.Application.Abstractions.IOperatorSeatRestoreOverrideRepository"/> only when
    /// that override was actually exercised, the identical split <see cref="OwnerModuleRevoke"/>'s own
    /// force/reason override already draws for its own table.</summary>
    OwnerOperatorSeatRestore,

    /// <summary>`25-76`: the platform owner adding a permission a tenant's role was missing - the fix
    /// for the drift this item's own "Found" names (seven live tenants, seven different `Admin`
    /// permission sets). Not a reuse of an existing member: unlike `23-86`'s own
    /// <see cref="OwnerModuleQuantityGrant"/> (a second input written into a row that member already
    /// names), this write reaches a table - `roles` - no existing <see cref="AccessRecordKind"/> member
    /// has ever named, so reusing one would misdescribe which row actually changed.</summary>
    OwnerRolePermissionsGrant,

    /// <summary>`25-77`: the platform owner removing a permission from a tenant's role - "no magic
    /// roles" (`docs/backlog/25-77-*.md`'s own "Answered": any permission, `Admin`'s own defining ones
    /// included, may be taken away). A separate member from <see cref="OwnerRolePermissionsGrant"/>
    /// right above, not a reuse of it, for the identical reason <see cref="OwnerModuleGrant"/> and
    /// <see cref="OwnerModuleRevoke"/> are two members rather than one: both write the same underlying
    /// table (`roles`, `enabled_modules` respectively), but a reviewer reading this deployment's own
    /// access log needs to tell "the owner widened what a role can do" apart from "the owner narrowed
    /// it" without opening each row's own resource details - the direction of an owner-only override is
    /// exactly the fact `AccessRecordKind` already distinguishes for every other reversible act it
    /// covers, and a role-permission write is no different.</summary>
    OwnerRolePermissionsRemoval,

    /// <summary>`25-181`: the platform owner granting a tenant extra seats of one role, by hand, beyond
    /// what the tariff includes - <see cref="Domain.OwnerSeatGrant"/>'s own write, the identical "the
    /// owner acted, record who/why" shape <see cref="OwnerModuleQuantityGrant"/> already establishes for
    /// its own (site, module) grant, restated here for the (site, role) one this item adds rather than
    /// reusing that member: a reviewer reading this deployment's own access log needs to tell "the owner
    /// widened a module's quantity" apart from "the owner widened a seat limit" without opening each
    /// row's own resource details.</summary>
    OwnerSeatGrant,
}
