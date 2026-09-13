using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.HasPendingOperatorInvite;

/// <summary>
/// `25-73`: deliberately no <see cref="IPermissionChecker"/> call and no `Result`/`Error` - there is
/// nothing to authorize (the email checked is always the caller's own) and no way for this query to
/// fail beyond an infrastructure fault, the identical "a plain bool, no wrapping needed" shape a read
/// this simple does not need to complicate.
/// </summary>
public sealed class HasPendingOperatorInviteHandler(IPendingOperatorInviteByEmailReadStore invites, IClock clock)
{
    public Task<bool> HandleAsync(HasPendingOperatorInvite query, CancellationToken cancellationToken) =>
        invites.AnyPendingForEmailAsync(query.Email, clock.UtcNow, cancellationToken);
}
