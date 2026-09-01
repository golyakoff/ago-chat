using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>One deleted attachment row, before its storage objects are cleaned up.</summary>
public sealed record DeletedAttachment(Guid Id, string ObjectKey, string? ThumbnailKey);

/// <summary>
/// `13-06`/`adr/0031`'s Decision 4 ("attachments follow their message's window"), applied to the exact
/// attachment ids <see cref="MessagePartitionPruneQuery.ListReferencedAttachmentIdsAsync"/> reads off a
/// slice's own rows just before <see cref="MessagePartitionPruneJob"/> removes them. Reuses `5-04`'s own
/// atomic-delete-then-clean-up-storage shape (<see cref="AttachmentOrphanSweepQuery"/>'s own remarks:
/// "one `DELETE ... RETURNING` statement... the row is already gone by the time storage cleanup
/// runs") rather than a second deletion technique - the predicate differs (an explicit id list here,
/// a state+age filter there), the mechanism does not.
/// </summary>
public static class AttachmentRetentionSweepQuery
{
    // Chunked rather than one statement for an arbitrarily large id list - the same reason every
    // other batch-oriented query in this codebase's retention/pruning family bounds its own work
    // instead of one unbounded statement (MessagePartitionPruneQuery.DeleteMessageBatchAsync's own
    // bounded-batch reasoning, applied here to a different table).
    private const int ChunkSize = 500;

    public static async Task<IReadOnlyList<DeletedAttachment>> DeleteByIdsAsync(
        NpgsqlConnection connection, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken)
    {
        if (attachmentIds.Count == 0)
        {
            return [];
        }

        var deleted = new List<DeletedAttachment>();
        foreach (var chunk in attachmentIds.Chunk(ChunkSize))
        {
            const string sql = """
                DELETE FROM attachments
                WHERE id = ANY(@ids)
                RETURNING id, object_key, thumbnail_key
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("ids", chunk);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                deleted.Add(new DeletedAttachment(
                    reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        return deleted;
    }
}
