using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ResolveOperatorIdentity;

/// <summary>
/// `23-71`: the one question both callers that decide sign-in eligibility ask -
/// <see cref="ResolveOperatorIdentityHandler"/> (which row, if any, may this token resolve to) and
/// <c>ListMyTenanciesHandler</c> (which of this identity's tenancies may it actually switch into) -
/// shared here rather than each composing <see cref="IPermissionChecker"/> and
/// <see cref="Operator.CanSignIn"/> separately. Unlike the seeded-role-literal restatements elsewhere
/// in this codebase (<c>RegisterSiteHandler</c>'s own remarks on why those are deliberately not
/// shared), this is the actual sign-in gate `decisions/0006` names - two independently maintained
/// copies of a security-relevant boolean is exactly the drift risk a shared helper removes, not the
/// harmless restatement that seed-data literal is.
///
/// <para>Lives in Application, not Domain: it composes <see cref="IPermissionChecker"/>, a real
/// infrastructure-backed port, which is exactly what <see cref="Operator.CanSignIn"/>'s own remarks
/// say Domain must never depend on directly.</para>
/// </summary>
public static class OperatorSignInEligibility
{
    /// <summary>Short-circuits on <see cref="Operator.HoldsSeat"/> before ever calling
    /// <paramref name="permissions"/> - the ordinary seated sign-in (the overwhelming majority of
    /// calls) costs no extra query beyond what this resolution path already made before this item.
    /// Only a seatless row pays for the <see cref="Permission.SiteManageOperators"/> lookup that
    /// decides whether it may sign in anyway.</summary>
    public static async Task<bool> CanSignInAsync(
        Operator candidate, IPermissionChecker permissions, CancellationToken cancellationToken)
    {
        if (candidate.HoldsSeat)
        {
            return true;
        }

        var isAdministrator = await permissions.HasPermissionAsync(
            candidate.Id, candidate.SiteId, Permission.SiteManageOperators, cancellationToken);
        return candidate.CanSignIn(isAdministrator);
    }
}
