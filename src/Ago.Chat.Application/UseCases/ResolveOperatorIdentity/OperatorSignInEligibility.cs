using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ResolveOperatorIdentity;

/// <summary>
/// `23-71`: the one question both callers that decide sign-in eligibility ask -
/// <see cref="ResolveOperatorIdentityHandler"/> (which row, if any, may this token resolve to) and
/// <c>ListMyTenanciesHandler</c> (which of this identity's tenancies may it actually switch into) -
/// shared here rather than each composing the same lookup separately. Unlike the seeded-role-literal
/// restatements elsewhere in this codebase (<c>RegisterSiteHandler</c>'s own remarks on why those are
/// deliberately not shared), this is the actual sign-in gate `decisions/0006` names - two independently
/// maintained copies of a security-relevant boolean is exactly the drift risk a shared helper removes,
/// not the harmless restatement that seed-data literal is.
///
/// <para>Lives in Application, not Domain: it composes <see cref="IOperatorRoleRepository"/>, a real
/// infrastructure-backed port, which is exactly what <see cref="Operator.CanSignIn"/>'s own remarks say
/// Domain must never depend on directly.</para>
///
/// <para><b>`25-170`: <see cref="IPermissionChecker"/> is gone from this method entirely.</b> The
/// Admin-permission exemption <see cref="Operator.CanSignIn"/> used to need
/// (`holdsManageOperatorsPermission`) existed only because "holds a seat" was one flag per account, so a
/// seatless Administrator needed a second door in. Now that a seat is a fact about one
/// `(operator, role)` pairing, an Administrator's own Admin-role row simply holds its own seat like any
/// other role assignment - there is no exemption left to compute, so this method has nothing left to ask
/// <see cref="IPermissionChecker"/> at all.</para>
/// </summary>
public static class OperatorSignInEligibility
{
    public static async Task<bool> CanSignInAsync(
        Operator candidate, IOperatorRoleRepository operatorRoles, CancellationToken cancellationToken)
    {
        var anyRoleHoldsSeat = await operatorRoles.HoldsAnySeatAsync(candidate.Id, cancellationToken);
        return candidate.CanSignIn(anyRoleHoldsSeat);
    }
}
