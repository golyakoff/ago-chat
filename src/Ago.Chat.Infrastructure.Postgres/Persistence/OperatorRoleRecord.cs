using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>The join between an operator and a <see cref="RoleRecord"/> - an operator can hold more
/// than one role, even though Stage 1 only ever grants the single seeded <c>"Operator"</c> role.</summary>
internal sealed class OperatorRoleRecord
{
    public OperatorId OperatorId { get; set; }
    public Guid RoleId { get; set; }
}
