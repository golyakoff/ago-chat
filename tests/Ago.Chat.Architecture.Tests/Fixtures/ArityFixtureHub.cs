using Microsoft.AspNetCore.SignalR;

namespace Ago.Chat.Architecture.Tests.Fixtures;

/// <summary>
/// `5-19`: a hub that exists only so <see cref="HubContractTests.TheRule_CountsTheParametersAClientWouldHaveToSupply"/>
/// can watch the arity scanner count something whose answer is known by inspection.
///
/// <para>The same permanent-fixture technique `0-02` used for layering and `17-01` for tenant
/// scoping. It matters more than usual here: the rule this fixture checks was written *because* a
/// green test suite was trusted once already, and a rule that has only ever been seen passing is
/// exactly what was trusted.</para>
///
/// <para>Never in <c>TestAssemblies.Api</c>, so it cannot make the real rule red - the scanner is
/// pointed at this test assembly explicitly for that one test.</para>
/// </summary>
internal sealed class ArityFixtureHub : Hub
{
    /// <summary>
    /// **The shape `14-06` got wrong.** Two required and two optional, which a C# caller may invoke
    /// with two arguments and a SignalR client may not: the wire needs four. If the scanner ever
    /// reports two for this, the rule has stopped measuring the thing that breaks clients.
    /// </summary>
    public Task WithOptionalTrailingParametersAsync(Guid id, string body, Guid? attachment = null, Guid? clientId = null) =>
        Task.FromResult((id, body, attachment, clientId));

    public Task WithOneParameterAsync(Guid id) => Task.FromResult(id);

    /// <summary>SignalR binds a trailing <see cref="CancellationToken"/> from the connection, not
    /// from the invocation, so a client supplies one fewer argument than the signature declares -
    /// the one case where "count the parameters" would over-count.</summary>
    public Task WithACancellationTokenAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult((id, cancellationToken));

    /// <summary>Excluded from the contract: SignalR calls this, never a client.</summary>
    public override Task OnConnectedAsync() => Task.CompletedTask;
}
