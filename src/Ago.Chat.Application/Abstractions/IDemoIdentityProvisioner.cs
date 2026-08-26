using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `8-07`/`adr/0058`: creates and removes the Keycloak user behind a minted demo operator, and is the
/// only thing in this codebase that writes to an identity provider.
///
/// <para><b>Why a port at all, when there is one implementation.</b> Not for a second provider - there
/// is no plausible one. For the reason `clean-architecture.md` puts every external resource behind a
/// port: <c>MintDemoTenantHandler</c> is where the interesting decisions live (the cap, the rate limit,
/// the ordering of the two writes), and every one of them is untestable if reaching them requires a
/// Keycloak. The real adapter is proven separately against a real Keycloak
/// (`DemoIdentityProvisionerTests`), which is a different test with a different cost.</para>
///
/// <para><b>Deliberately narrow.</b> Two methods, both about a demo identity. It is not
/// `IKeycloakAdminClient`: the credential behind it is scoped to `manage-users` and the port is scoped
/// to match, so nothing can quietly grow a "while we are here, also read the realm" call. `13-01`'s
/// invitations are the item that widens this, and widening it is a decision that should have to be
/// made rather than be available.</para>
/// </summary>
public interface IDemoIdentityProvisioner
{
    /// <summary>
    /// Creates an enabled realm user and returns <b>the subject id Keycloak assigned it</b>.
    ///
    /// <para><b>The caller does not choose the id, and that was measured rather than assumed.</b> This
    /// port was first written the other way round - caller-chosen id, so the operator row could be
    /// written before the identity and a half-failure would self-heal. Keycloak refuses it: `POST
    /// /admin/realms/{realm}/users` with an `id` in the body answers `409 Conflict`, on 21.1 and on the
    /// 26.0 the deployment actually runs - both checked (`DemoTenantLifecycleTests`). So the id comes
    /// back from the `Location` header instead, and <c>MintDemoTenantHandler</c> compensates rather than
    /// ordering its way out of the problem - see its own remarks and `adr/0058`.</para>
    ///
    /// <para>Returns a failed <see cref="Result{T}"/> rather than throwing for an outcome the caller can
    /// act on (the identity provider refusing the username, for instance). An unreachable Keycloak
    /// throws, because that is an infrastructure fault and the resilience pipeline around the adapter
    /// is what acts on it - the same split <c>coding-style.md</c> draws everywhere else.</para>
    /// </summary>
    Task<Result<string>> CreateAsync(string username, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the user, by subject id - a string, because that is what `Operator.ExternalSubjectId`
    /// holds (`adr/0022`: a Keycloak `sub`) and what Keycloak's own admin API takes in the path.
    /// <b>Idempotent by contract</b>: a subject that is already gone
    /// is a success, not a failure. The expiry sweeper retries, and a sweeper that could be permanently
    /// blocked by a user an operator deleted by hand would leave rows behind forever - which is the
    /// one outcome `8-07`'s Done-when is about.
    /// </summary>
    Task DeleteAsync(string subjectId, CancellationToken cancellationToken);
}
