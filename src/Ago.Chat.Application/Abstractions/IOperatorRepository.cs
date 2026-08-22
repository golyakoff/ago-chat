using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `5-05`: shaped around the one thing that needs it - resolving a validated OIDC principal back to
/// an operator (`adr/0022`) - not a general operator CRUD port. Grow this only when a second real
/// caller needs a different question answered.
/// </summary>
public interface IOperatorRepository
{
    Task<Operator?> GetByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken);
}
