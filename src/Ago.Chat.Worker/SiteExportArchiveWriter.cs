using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ago.Platform.Abstractions;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `16-03`: builds one tenant's export archive - the format `adr/0031` calls "load-bearing twice
/// over" (this item's own export, and `13-06`'s future retention archive). A `.zip` of `manifest.json`
/// plus one file per store, most of them JSON Lines (one JSON object per line, never a JSON array) so
/// a reader - human or `13-06`'s own archive reader - can process a store without loading the whole
/// file into memory either, the same streaming property this writer itself has on the write side.
///
/// <para><b>Every per-store read is a forward-only <see cref="NpgsqlDataReader"/> loop, never
/// <c>ToListAsync</c>/Dapper's buffered <c>QueryAsync</c>.</b> A tenant's row count is unbounded
/// (`16-03`'s own brief: "a tenant with a year of conversations must not require the API to hold it
/// all in memory") - each method below reads one row, serialises it, writes one line, and moves on;
/// at most one row's worth of data is ever in memory for a given store, the same "raw Npgsql,
/// forward-only" shape <see cref="SiteErasureQuery"/>/<see cref="ConversationErasureQuery"/> already
/// establish for exactly this reason.</para>
///
/// <para><b>Format versioning.</b> <c>manifest.json</c> carries an explicit
/// <see cref="ManifestDocument.FormatVersion"/>, starting at 1. Recorded as a deliberate choice, not a
/// default: `adr/0031` makes this format the on-disk shape of `13-06`'s retention archive too, so a
/// later change to any row shape here is a change to files already written under an earlier version -
/// exactly the situation a format version exists to let a future reader detect and branch on. The
/// field costs nothing today and its absence would cost real ambiguity the first time this shape ever
/// changes, so it is added now rather than deferred to whichever change turns out to need it
/// first.</para>
///
/// <para><b>Attachment bytes: referenced by presigned URL, not embedded.</b> Recorded here because it
/// is this class's own concrete consequence, reasoned through fully in `RequestSiteExportHandler`'s
/// neighbouring notes and this item's own commit-prep report: <see cref="IFileStorage"/> has no
/// method that returns attachment bytes to a caller at all (`file-storage.md`'s governing rule - "file
/// bytes never pass through the API process" - and `CreateDownloadUrlAsync` is the only read path the
/// port exposes), so embedding bytes would mean adding a new platform-port method, an `ago-platform`
/// change this single-repository item does not make. Referencing by URL needs nothing new and matches
/// the rule's own spirit even though `Ago.Chat.Worker` is not `Ago.Chat.Api`. The cost, stated plainly
/// in the manifest and in <c>docs/architecture/file-storage.md</c>: the export decays - a presigned
/// URL embedded today stops working once <see cref="SiteExportJobOptions.AttachmentUrlLifetime"/>
/// elapses, while every other file in the archive remains readable forever.</para>
///
/// <para><b>What this item deliberately does not include.</b> <c>webhook_deliveries</c> is
/// site-scoped personal data per `personal-data.md`'s own table, but the backlog's Contents list
/// enumerates conversations, messages, attachment metadata, site configuration and the operator list
/// and does not name it - a deliberate reading of a deliberate enumeration, not an oversight: delivery
/// history is operational/integration log data about the tenant's own webhook receiver (what was
/// sent, to which endpoint, and how it responded), not conversation data a subject access request is
/// normally understood to cover. <c>channel_credentials</c> (bot tokens, webhook secrets) is excluded
/// for a different reason - it holds no personal data at all, only the tenant's own integration
/// secrets, which is precisely the "exclude anything secret-shaped in site config" instruction this
/// item's own brief gives for <c>sites</c>. Both calls are stated here rather than left for a future
/// reader to wonder whether they were considered.</para>
/// </summary>
public sealed class SiteExportArchiveWriter(IFileStorage fileStorage, SiteExportJobOptions options)
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(
        NpgsqlConnection connection, ZipArchive archive, Guid siteId, DateTimeOffset exportedAt, CancellationToken cancellationToken)
    {
        await WriteManifestAsync(archive, siteId, exportedAt, cancellationToken);
        await WriteSiteAsync(archive, connection, siteId, cancellationToken);
        await WriteOperatorsAsync(archive, connection, siteId, cancellationToken);
        await WriteVisitorsAsync(archive, connection, siteId, cancellationToken);
        await WriteChannelIdentitiesAsync(archive, connection, siteId, cancellationToken);
        await WriteConversationsAsync(archive, connection, siteId, cancellationToken);
        await WriteMessagesAsync(archive, connection, siteId, cancellationToken);
        await WriteAttachmentsAsync(archive, connection, siteId, exportedAt, cancellationToken);
    }

    private static async Task WriteManifestAsync(
        ZipArchive archive, Guid siteId, DateTimeOffset exportedAt, CancellationToken cancellationToken)
    {
        var manifest = new ManifestDocument(
            FormatVersion,
            siteId,
            exportedAt,
            AttachmentBytes: "referenced-by-url",
            Stores: ["site", "operators", "visitors", "channelIdentities", "conversations", "messages", "attachments"]);

        var entry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, manifest, JsonOptions, cancellationToken);
    }

    private static async Task WriteSiteAsync(
        ZipArchive archive, NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken)
    {
        const string sql = "select id, name, allowed_origins from sites where id = @siteId";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId);

        SiteExportRow? row = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                row = new SiteExportRow(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<string[]>(2));
            }
        }

        var entry = archive.CreateEntry("site.json", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, row, JsonOptions, cancellationToken);
    }

    private static async Task WriteOperatorsAsync(
        ZipArchive archive, NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, status, capacity, active_chats, external_subject_id
            from operators
            where site_id = @siteId
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId);

        var entry = archive.CreateEntry("operators.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new OperatorExportRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private static async Task WriteVisitorsAsync(
        ZipArchive archive, NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken)
    {
        const string sql = "select id, first_seen_at, last_seen_at from visitors where site_id = @siteId order by id";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId);

        var entry = archive.CreateEntry("visitors.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new VisitorExportRow(
                reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private static async Task WriteChannelIdentitiesAsync(
        ZipArchive archive, NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, visitor_id, kind, external_address, first_seen_at, last_seen_at
            from channel_identities
            where site_id = @siteId
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId);

        var entry = archive.CreateEntry("channel_identities.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new ChannelIdentityExportRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private static async Task WriteConversationsAsync(
        ZipArchive archive, NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, visitor_id, operator_id, state, created_at, last_sequence,
                   operator_unread_count, visitor_unread_count
            from conversations
            where site_id = @siteId
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId);

        var entry = archive.CreateEntry("conversations.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new ConversationExportRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private static async Task WriteMessagesAsync(
        ZipArchive archive, NpgsqlConnection connection, Guid siteId, CancellationToken cancellationToken)
    {
        // Joined through conversations - messages carries no site_id of its own (it is partitioned by
        // created_at, not site, adr/0031's own multi-level partitioning is not built yet). Ordered by
        // (conversation_id, sequence) so a reader sees each conversation's own messages in the order
        // they were sent, the natural reading order for a transcript - the same leading index columns
        // ConversationErasureQuery.DeleteMessageBatchAsync's own remarks describe.
        const string sql = """
            select m.id, m.conversation_id, m.sequence, m.author_id, m.author_kind, m.created_at,
                   m.body, m.content_kind, m.content, m.actions, m.attachment_id
            from messages m
            join conversations c on c.id = m.conversation_id
            where c.site_id = @siteId
            order by m.conversation_id, m.sequence
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId);

        var entry = archive.CreateEntry("messages.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // content/actions are exported as opaque strings, never parsed - the same
            // "AGO Chat stores, sequences, delivers and renders it and never reads inside" rule
            // personal-data.md states for these two columns (adr/0061). Re-serialising them as nested
            // JSON nodes here would mean this writer parsing a payload schema it must not own.
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

    private async Task WriteAttachmentsAsync(
        ZipArchive archive, NpgsqlConnection connection, Guid siteId, DateTimeOffset exportedAt, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, conversation_id, message_id, object_key, thumbnail_key, content_type, size_bytes, state, created_at
            from attachments
            where site_id = @siteId
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId);

        var entry = archive.CreateEntry("attachments.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        var lifetime = options.AttachmentUrlLifetime;
        var downloadUrlExpiresAt = exportedAt + lifetime;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var objectKey = reader.GetString(3);
            var thumbnailKey = reader.IsDBNull(4) ? null : reader.GetString(4);

            // Presigning is a local signature computation (SigV4), not a network round trip - one per
            // row here costs nothing like the "external I/O per item" this codebase is normally
            // careful about (SiteErasureJob's own per-operator Keycloak calls, by contrast, really are
            // network calls).
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
        int FormatVersion, Guid SiteId, DateTimeOffset ExportedAt, string AttachmentBytes, IReadOnlyList<string> Stores);

    private sealed record SiteExportRow(Guid Id, string Name, IReadOnlyList<string> AllowedOrigins);

    private sealed record OperatorExportRow(Guid Id, string Status, int Capacity, int ActiveChats, string? ExternalSubjectId);

    private sealed record VisitorExportRow(Guid Id, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

    private sealed record ChannelIdentityExportRow(
        Guid Id, Guid VisitorId, string Kind, string ExternalAddress, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

    private sealed record ConversationExportRow(
        Guid Id, Guid VisitorId, Guid? OperatorId, string State, DateTimeOffset CreatedAt, int LastSequence,
        int OperatorUnreadCount, int VisitorUnreadCount);

    private sealed record MessageExportRow(
        Guid Id, Guid ConversationId, int Sequence, Guid AuthorId, string AuthorKind, DateTimeOffset CreatedAt,
        string Body, string? ContentKind, string? Content, string? Actions, Guid? AttachmentId);

    private sealed record AttachmentExportRow(
        Guid Id, Guid ConversationId, Guid? MessageId, string ContentType, long SizeBytes, string State,
        DateTimeOffset CreatedAt, Uri DownloadUrl, Uri? ThumbnailDownloadUrl, DateTimeOffset DownloadUrlExpiresAt);
}
