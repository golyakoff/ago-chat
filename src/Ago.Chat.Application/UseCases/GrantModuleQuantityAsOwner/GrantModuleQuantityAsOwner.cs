using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GrantModuleQuantityAsOwner;

/// <summary>
/// `23-66`/`adr/0093`/`adr/0150`: the platform owner's own write for a module's countable quantity -
/// the route <see cref="GrantModuleQuantity.GrantModuleQuantity"/> never got, because that command was
/// shaped for a tenant's own operator (<see cref="GrantModuleQuantity.GrantModuleQuantityHandler"/>
/// checks <see cref="Application.Abstractions.IPermissionChecker"/> against a
/// <see cref="Domain.OperatorId"/>) and the platform owner has neither. Carries no word of what a
/// "calendar" or a "master" is - the identical opacity <see cref="Domain.ModuleKey"/>'s own remarks
/// state for the key itself; the calendar add-on's own "N masters" is only the first real caller.
///
/// <para><b>A wholly separate command and handler from
/// <see cref="GrantModuleQuantity.GrantModuleQuantity"/>, not a nullable-<c>OperatorId</c> branch on
/// it.</b> The identical reasoning <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwner"/>'s
/// own remarks give for its own pair: the fact that authorizes this call (the <c>RequirePlatformOwner</c>
/// policy on the route that resolves this handler) does not live in a table
/// <see cref="Application.Abstractions.IPermissionChecker"/> could check, so a permission check here
/// would be a second, weaker copy of a decision the policy already made.</para>
///
/// <para><b>`23-88`: <see cref="ExpectedAffectedCount"/> is the write-time guard, added
/// beside the quantity itself rather than as a second call.</b> <see langword="null"/>
/// preserves this command's own original, unconditional behaviour exactly - a caller that
/// never asked for a preview (an older console build, or the tenant-facing sibling this
/// command mirrors) still applies the grant the way it always has, no regression. A real
/// value means the caller went through `23-88`'s own async preview round trip and is
/// confirming against a specific answer - see
/// <see cref="GrantModuleQuantityAsOwnerHandler"/>'s own remarks for what this handler does
/// with it.</para>
/// </summary>
public sealed record GrantModuleQuantityAsOwner(SiteId SiteId, string ModuleKey, int Quantity, int? ExpectedAffectedCount = null);
