using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.VerifyModuleRegistrationAsOwner;

/// <summary>
/// `23-83`/`adr/0151`: the platform owner's own half of `22-11`'s fourth Done-when ("the two sides
/// cannot silently disagree"), moved here once the tenant's own <c>VerifyModuleRegistration</c>
/// stopped existing as a route. See <see cref="RotateModuleCredentialAsOwner.RotateModuleCredentialAsOwner"/>'s
/// own remarks for why this is a separate command rather than a nullable-<see cref="OperatorId"/>
/// branch on the deleted tenant command - the identical reasoning applies verbatim.
/// </summary>
/// <param name="EntryPoint">Supplied by the caller, deliberately - see the deleted tenant command's
/// own remarks (`VerifyModuleRegistration`, `23-83`'s own report) for why reading it back off
/// <see cref="EnabledModule.EntryPoint"/> instead would trust exactly the row this check exists to
/// verify.</param>
public sealed record VerifyModuleRegistrationAsOwner(SiteId SiteId, string ModuleKey, string EntryPoint);

/// <param name="ChatHasRegistration">Whether <c>Ago.Chat.*</c> holds an <see cref="EnabledModule"/> row
/// for this (site, module) pair.</param>
/// <param name="ModuleHasRegistration">Whether the module deployment named by
/// <see cref="VerifyModuleRegistrationAsOwner.EntryPoint"/> reports one for this site.</param>
/// <param name="Agree"><see langword="true"/> exactly when both sides answer the same way.</param>
public readonly record struct ModuleRegistrationReconciliationResult(
    bool ChatHasRegistration, bool ModuleHasRegistration, bool Agree);
