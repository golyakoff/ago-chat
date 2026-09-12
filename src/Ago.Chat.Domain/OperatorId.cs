using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct OperatorId(Guid Value) : IStronglyTypedId
{
    /// <summary>
    /// `23-73`: the sentinel used wherever a write is attributed to "nobody, this system decided it" -
    /// a scheduled sweep requesting a site's own erasure for inactivity, with no operator behind it at
    /// all. The identical <see cref="Guid.Empty"/> sentinel <see cref="Conversation.SystemAuthorId"/>
    /// already uses for a system-authored message, for the identical reason: honest at the data level
    /// (a real, storable value, not a nullable column threaded through everything downstream) while
    /// remaining unambiguous that no real operator row exists at this id - no <c>operators</c> row is
    /// ever seeded with <see cref="Guid.Empty"/>, and nothing here needs one, since
    /// <c>erasure_records.requested_by</c> carries no foreign key back to <c>operators</c>
    /// (<c>ErasureRecordEntityConfiguration</c>'s own remarks on why that column names the requester
    /// without ever joining to prove they still exist).
    /// </summary>
    public static readonly OperatorId System = new(Guid.Empty);
}
