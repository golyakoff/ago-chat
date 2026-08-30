using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>The join between a <see cref="Conversation"/> and a <see cref="Tag"/> - the same shape
/// <see cref="OperatorRoleRecord"/> already establishes for operator/role. No `site_id` column, the
/// same reason `messages` carries none of its own (`ConversationReadStore`'s own remarks): this row is
/// only ever reached through a <see cref="ConversationId"/> that a caller has already had tenant-checked
/// one level up, or through a <see cref="TagId"/> a caller already resolved via
/// <c>ITagRepository.GetByIdAsync(id, siteId, ...)</c>.
///
/// <para><see cref="Source"/>: `19-02`'s own addition - see <see cref="TagSource"/>'s own remarks for
/// why this lives on the join row rather than on <see cref="Tag"/> itself.</para></summary>
internal sealed class ConversationTagRecord
{
    public ConversationId ConversationId { get; set; }
    public TagId TagId { get; set; }
    public TagSource Source { get; set; }
}
