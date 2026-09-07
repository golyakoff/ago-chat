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
/// </summary>
public sealed record GrantModuleQuantityAsOwner(SiteId SiteId, string ModuleKey, int Quantity);
