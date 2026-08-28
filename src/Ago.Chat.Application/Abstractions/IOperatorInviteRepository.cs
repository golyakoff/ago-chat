using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-01`: the write side of generating an <see cref="OperatorInvite"/> - shaped around
/// `CreateOperatorInviteHandler`'s one need, the same "its own aggregate, its own transaction boundary"
/// reasoning <see cref="IWebhookEndpointRepository"/>'s own remarks give for not folding into a
/// hypothetical `ISiteRepository` method. Redemption is a deliberately separate port
/// (<see cref="IOperatorInviteRedemptionRepository"/>) - it is not an ordinary load-mutate-save of this
/// aggregate, but one step inside a wider transaction that also creates an `Operator` row and locks a
/// `Site` row, the same "its own port because it writes across more than one aggregate" reasoning
/// <see cref="ISiteRegistrationRepository"/>'s own remarks give.
/// </summary>
public interface IOperatorInviteRepository
{
    Task SaveAsync(OperatorInvite invite, CancellationToken cancellationToken);
}
