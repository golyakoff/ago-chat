using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Architecture.Tests.Fixtures;

/// <summary>
/// `17-01`: the deliberate violation <see cref="TenantScopeTests.TheRule_FlagsAHandlerThatTakesASiteIdAndNeverChecksPermission"/>
/// runs the guard against - `0-02`'s own way of showing a rule can fail, kept in the build rather
/// than performed once by hand and described afterwards.
///
/// <para>These two live in this test project, never in <c>Ago.Chat.Application</c>: the guard scans
/// that assembly for real, so a violating handler shipped there would (correctly) turn the guard
/// red for everyone. They are written to be structurally indistinguishable from the real thing -
/// same primary-constructor shape, same <c>async</c> state machine, a command record carrying a
/// <c>SiteId</c> - because the rule reads IL, and a hand-written stand-in that happened to compile
/// differently would prove nothing about the code it is meant to police.</para>
/// </summary>
internal sealed record DoSomethingToASite(SiteId SiteId, OperatorId RequestedBy);

/// <summary>The mistake: a tenant-scoped command whose handler never asks whether this operator may
/// act on that site. Nothing here is called at runtime - its IL is the whole point.</summary>
internal sealed class ForgetfulTenantScopedHandler(ISiteRepository sites)
{
    public async Task<bool> HandleAsync(DoSomethingToASite command, CancellationToken cancellationToken)
    {
        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        return site is not null;
    }
}

/// <summary>The same use case done correctly, so a rule that flagged everything would fail the
/// demonstration too.</summary>
internal sealed class CompliantTenantScopedHandler(ISiteRepository sites, IPermissionChecker permissions)
{
    public async Task<bool> HandleAsync(DoSomethingToASite command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return false;
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        return site is not null;
    }
}
