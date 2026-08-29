using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `13-06`/`adr/0031`: builds one (site, retention class, period) archive object - reusing `16-03`'s
/// real, shipped archive format (<see cref="SiteExportArchiveWriter"/>'s own manifest/`.jsonl`
/// conventions and row shapes) rather than inventing a second one, per `adr/0031`'s own consequence
/// ("`16-03`'s export format becomes load-bearing twice over"). Narrower in scope than that writer -
/// a *period* archive holds only the messages a single leaf partition's rows and their attachments,
/// never the whole tenant - so it carries two stores (<c>messages</c>, <c>attachments</c>) instead of
/// seven, and reads its messages straight off the leaf partition table by name rather than joining
/// through <c>conversations</c> (`18-01`'s denormalized <c>site_id</c> is what makes that possible; it
/// did not exist yet when `16-03` wrote its own `messages` query the long way).
///
/// <para><b>Row shapes are deliberately re-declared here, not shared via a reference to
/// <see cref="SiteExportArchiveWriter"/>'s private nested records.</b> The two writers' rows are the
/// same *shape* (same field names, same order, same JSON output) by design - `MessageExportRow`/
/// `AttachmentExportRow` below are intentionally identical to that class's own - but duplicating one
/// small, stable, already-shipped shape across two files is a smaller, lower-risk change than widening
/// a merged, working file's `private` records to `internal` for a second caller. If a third writer
/// ever needs the identical shape, that is the point at which extracting a shared file stops being
/// premature.</para>
///
/// <para><b>Attachments are referenced by presigned URL, not embedded</b> - the identical trade-off
/// <see cref="SiteExportArchiveWriter"/>'s own remarks state, and the identical reason (<see cref="IFileStorage"/>
/// has no byte-returning read method). The decay is real and stated plainly in this item's own report:
/// unlike a tenant's own on-demand export, whose link merely *expires* after
/// <see cref="MessageArchiveJobOptions.AttachmentUrlLifetime"/>, this archive's attachment links can go
/// permanently dead much sooner than that, the moment <see cref="MessagePartitionPruneJob"/>'s own
/// attachment sweep deletes the underlying object for the very partition this archive was built
/// from.</para>
/// </summary>
public sealed class MessageArchiveWriter(IFileStorage fileStorage, MessageArchiveJobOptions options)
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(
        NpgsqlConnection connection, ZipArchive archive, string partitionName, Guid siteId, RetentionClass retentionClass,
        DateOnly periodStart, DateOnly periodEnd, DateTimeOffset archivedAt, CancellationToken cancellationToken)
    {
        await WriteManifestAsync(archive, siteId, retentionClass, periodStart, periodEnd, archivedAt, cancellationToken);
        await WriteMessagesAsync(archive, connection, partitionName, siteId, cancellationToken);
        await WriteAttachmentsAsync(archive, connection, siteId, periodStart, periodEnd, archivedAt, cancellationToken);
    }

    private static async Task WriteManifestAsync(
        ZipArchive archive, Guid siteId, RetentionClass retentionClass, DateOnly periodStart, DateOnly periodEnd,
        DateTimeOffset archivedAt, CancellationToken cancellationToken)
    {
        var manifest = new ManifestDocument(
            FormatVersion, siteId, retentionClass.Value, periodStart, periodEnd, archivedAt,
            AttachmentBytes: "referenced-by-url",
            Stores: ["messages", "attachments"]);

        var entry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, manifest, JsonOptions, cancellationToken);
    }

    /// <summary>Reads the leaf partition table directly, by name - not the `messages` parent filtered
    /// by `site_id`/`retention_class`/`created_at`. Querying the exact table this archive is standing
    /// in for `DROP`ping is a stronger guarantee than trusting the planner to prune to it, and it is
    /// also simply faster: no need to re-derive the partition bounds as a `WHERE` predicate the
    /// planner then has to prove is equivalent to the partition boundary it already knows.</summary>
    private static async Task WriteMessagesAsync(
        ZipArchive archive, NpgsqlConnection connection, string partitionName, Guid siteId, CancellationToken cancellationToken)
    {
        var sql = $"""
            select id, conversation_id, sequence, author_id, author_kind, created_at, body, content_kind, content, actions, attachment_id
            from {partitionName}
            where site_id = @siteId
            order by conversation_id, sequence
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId);

        var entry = archive.CreateEntry("messages.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // `16-03`'s own rule applies verbatim: content/actions are opaque strings, never parsed
            // (adr/0061).
            var row = new MessageExportRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt32(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    /// <summary>Attachments are matched by `site_id` and `created_at` falling within the same period
    /// this archive covers - not, as it might first appear the same reasoning
    /// <see cref="MessagePartitionPruneQuery.ListReferencedAttachmentIdsAsync"/>'s own remarks warn
    /// against, a source of the ambiguity that method exists to avoid. That method's concern is
    /// *deletion* correctness (never delete an attachment whose owning class's partition has not
    /// actually been dropped yet); this is *inclusion* in a read-only archive, where the cost of
    /// listing an attachment that also happens to appear in a sibling class's own later archive is
    /// nothing worse than a harmless duplicate reference in two files - both true, neither
    /// destructive.</summary>
    private async Task WriteAttachmentsAsync(
        ZipArchive archive, NpgsqlConnection connection, Guid siteId, DateOnly periodStart, DateOnly periodEnd,
        DateTimeOffset archivedAt, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, conversation_id, message_id, object_key, thumbnail_key, content_type, size_bytes, state, created_at
            from attachments
            where site_id = @siteId and created_at >= @periodStart and created_at < @periodEnd
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId);
        command.Parameters.AddWithValue("periodStart", periodStart.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("periodEnd", periodEnd.ToDateTime(TimeOnly.MinValue));

        var entry = archive.CreateEntry("attachments.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        var lifetime = options.AttachmentUrlLifetime;
        var downloadUrlExpiresAt = archivedAt + lifetime;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var objectKey = reader.GetString(3);
            var thumbnailKey = reader.IsDBNull(4) ? null : reader.GetString(4);

            var downloadUrl = await fileStorage.CreateDownloadUrlAsync(new ObjectKey(objectKey), lifetime, cancellationToken);
            Uri? thumbnailUrl = thumbnailKey is null
                ? null
                : await fileStorage.CreateDownloadUrlAsync(new ObjectKey(thumbnailKey), lifetime, cancellationToken);

            var row = new AttachmentExportRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                downloadUrl,
                thumbnailUrl,
                downloadUrlExpiresAt);
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private sealed record ManifestDocument(
        int FormatVersion, Guid SiteId, string RetentionClass, DateOnly PeriodStart, DateOnly PeriodEnd,
        DateTimeOffset ArchivedAt, string AttachmentBytes, IReadOnlyList<string> Stores);

    // Intentionally identical shape to SiteExportArchiveWriter's own private MessageExportRow/
    // AttachmentExportRow - see this class's own remarks for why that duplication is deliberate.
    private sealed record MessageExportRow(
        Guid Id, Guid ConversationId, int Sequence, Guid AuthorId, string AuthorKind, DateTimeOffset CreatedAt,
        string Body, string? ContentKind, string? Content, string? Actions, Guid? AttachmentId);

    private sealed record AttachmentExportRow(
        Guid Id, Guid ConversationId, Guid? MessageId, string ContentType, long SizeBytes, string State,
        DateTimeOffset CreatedAt, Uri DownloadUrl, Uri? ThumbnailDownloadUrl, DateTimeOffset DownloadUrlExpiresAt);
}
