namespace Ago.Chat.Domain;

/// <summary>
/// `24-12`: which table <c>access_records.resource_id</c> points into, when the access named a
/// specific row rather than a whole tenant or the whole deployment - the same "a bare id column needs
/// a discriminator to say what it holds" reasoning <see cref="AccessRecordActorKind"/>'s own remarks
/// give, applied to the resource side of the same row instead of the actor side.
///
/// <para><see langword="null"/> (no member at all) covers the two access kinds with no single
/// resource: <see cref="AccessRecordKind.OwnerSiteList"/> (every tenant, not one) and
/// <see cref="AccessRecordKind.OwnerSiteDetail"/> (the site itself is already named by
/// <c>access_records.site_id</c>, so a second, redundant pointer to the same row would say nothing a
/// reader could not already read off <c>site_id</c>).</para>
/// </summary>
public enum AccessRecordResourceKind
{
    /// <summary><see cref="AccessRecordKind.CrossConversationHistoryRead"/>'s own resource - the
    /// historical conversation that was opened, not the conversation the requesting operator was
    /// actually assigned to.</summary>
    Conversation,

    /// <summary><see cref="AccessRecordKind.OwnerChannelIdentityUnlink"/>'s own resource.</summary>
    ChannelIdentity,

    /// <summary><see cref="AccessRecordKind.OwnerModuleGrant"/>/<see cref="AccessRecordKind.OwnerModuleRevoke"/>'s
    /// own resource.</summary>
    EnabledModule,

    /// <summary>`23-66`: <see cref="AccessRecordKind.OwnerModuleQuantityGrant"/>'s own resource - a
    /// different row (<c>module_quantity_grants</c>) from <see cref="EnabledModule"/>, keyed by
    /// (site, module) rather than by a synthetic id (`Domain.ModuleQuantityGrant`'s own remarks).
    /// </summary>
    ModuleQuantityGrant,

    /// <summary>`23-68`: <see cref="AccessRecordKind.OwnerOperatorSeatRestore"/>'s own resource - the
    /// <c>operators</c> row whose seat was restored, named by its own <c>OperatorId</c>. A real
    /// resource id, unlike <see cref="AccessRecordKind.OwnerSiteDetail"/>'s deliberate absence of
    /// one - restoring a seat acts on one specific operator among possibly several on the same site,
    /// so <c>site_id</c> alone would not say which.</summary>
    Operator,

    /// <summary>`25-76`: <see cref="AccessRecordKind.OwnerRolePermissionsGrant"/>'s own resource - the
    /// `roles` row whose permission list was widened, named by its own <c>Id</c>
    /// (<see cref="Ago.Chat.Application.Abstractions.RoleLookup.Id"/>) - a real resource id, the same
    /// "act on one specific row among possibly several on the same site" shape <see cref="Operator"/>'s
    /// own remarks give for the identical reason (a site always has more than one role, so `site_id`
    /// alone would not say which).</summary>
    Role,

    /// <summary>`25-181`: <see cref="AccessRecordKind.OwnerSeatGrant"/>'s own resource - a different
    /// row (<c>owner_seat_grants</c>) from every member above, keyed by (site, role) rather than by a
    /// synthetic id, the identical "no resourceId" shape <see cref="ModuleQuantityGrant"/> already
    /// establishes for its own (site, module) key.</summary>
    OwnerSeatGrant,
}
