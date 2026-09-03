using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.VerifyModuleRegistration;

/// <summary>
/// `22-11`'s own fourth Done-when: "the two sides cannot silently disagree: a registration that exists
/// on one side only is detectable." This is that detector, from Chat's own side - see
/// <see cref="VerifyModuleRegistrationHandler"/>'s own remarks for exactly what it can and cannot
/// prove.
/// </summary>
/// <param name="EntryPoint">Supplied by the caller rather than read from this site's own
/// <see cref="EnabledModule.EntryPoint"/>, deliberately: the row whose correctness this check exists to
/// verify is exactly the row a "read it from itself" shortcut would have to trust. An operator who
/// suspects drift already knows which module deployment they configured - the same coordinate they
/// would have typed into <c>EnableModuleForSite</c> in the first place - and supplies it again here.
/// When <see cref="EnabledModule"/> genuinely has no row for this site at all, there would be nothing
/// to read anyway.</param>
public sealed record VerifyModuleRegistration(
    OperatorId RequestedBy, SiteId SiteId, string ModuleKey, string EntryPoint, string ProvisioningSecret);

/// <param name="ChatHasRegistration">Whether <c>Ago.Chat.*</c> holds an <see cref="EnabledModule"/> row
/// for this (site, module) pair.</param>
/// <param name="ModuleHasRegistration">Whether the module deployment named by
/// <see cref="VerifyModuleRegistration.EntryPoint"/> reports one for this site.</param>
/// <param name="Agree"><see langword="true"/> exactly when both sides answer the same way - the one
/// value an operator or an automated check actually needs to act on.</param>
public readonly record struct ModuleRegistrationReconciliationResult(
    bool ChatHasRegistration, bool ModuleHasRegistration, bool Agree);
