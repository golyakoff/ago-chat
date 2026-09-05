using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `24-11`: builds one subject-scoped export archive - the port implementation
/// <see cref="IPersonExportArchiveWriter"/>'s own remarks describe, and the narrower sibling of
/// `Ago.Chat.Worker`'s `SiteExportArchiveWriter`. Same wire format: one `.zip`, `manifest.json` plus
/// one JSON Lines file per store, `formatVersion: 1` (the same number, since the *shape* - zip of
/// manifest+JSONL - is unchanged; only which rows a given archive holds differs, and that is exactly
/// what `manifest.json`'s own `scope`/`visitorId`/`conversationIds` fields communicate).
///
/// <para><b>What this archive deliberately does not include, and why - written here and in
/// `manifest.json` itself so a tenant reading the very file they received can see the boundary, not
/// only a reader of this source file.</b></para>
/// <list type="bullet">
/// <item><description><c>operators</c> - the whole-site export's own roster of every operator on the
/// tenant would put a *different* person's data (the operator's own identity) into an artifact meant
/// to answer one visitor's request. That is the exact disclosure problem `24-11` exists to close, applied
/// to the other class of subject a site holds. A message's own `author_id` still appears in
/// `messages.jsonl` (an opaque id, not a name) - the same "an id with no name resolved is not personal
/// data about the operator" reasoning `personal-data.md` already gives for `conversation_assignments`.</description></item>
/// <item><description><c>notes</c> (`18-04`'s `conversation_notes`) - an operator's private annotation
/// *about* the visitor, written by someone else. `18-04` already made this structurally unreachable
/// from every visitor-facing read path; a subject-access export is exactly such a path, so the same
/// rule applies here rather than being silently relaxed for it. This is the backlog item's own named
/// judgement call, decided here as "excluded."</description></item>
/// <item><description><c>tags</c>/<c>conversation_tags</c> - an operator-chosen label vocabulary, tenant
/// workflow metadata rather than personal data about the visitor (`personal-data.md`'s own words for
/// `tags`: "not itself personal data the way a note is"). Not named in `24-11`'s own Scope enumeration
/// ("the transcripts, the contact details, the channel identity"), so left out rather than added by
/// assumption.</description></item>
/// <item><description><c>site</c> (tenant configuration) - not personal data about the visitor at all;
/// the whole-site export's `site.json` exists to satisfy tenant *portability* (`16-03`'s own framing),
/// a question this narrower, subject-access-shaped export does not need to answer.</description></item>
/// </list>
///
/// <para><b>Attachment bytes: referenced by presigned URL, not embedded - unchanged from `adr/0072`.</b>
/// Same reasoning, same port (<see cref="IFileStorage"/> has no byte-returning method), same 7-day
/// SigV4 ceiling. Reusing <see cref="PersonExportOptions.AttachmentUrlLifetime"/> rather than
/// <c>SiteExportJobOptions</c>'s own field - a separate options class because this writer is
/// Infrastructure, `SiteExportJobOptions` is declared in `Ago.Chat.Worker` (a host project this
/// assembly must not reference), not because the value should ever drift from it.</para>
/// </summary>
public sealed class PersonExportArchiveWriter(NpgsqlDataSource dataSource, IFileStorage fileStorage, PersonExportOptions options)
    : IPersonExportArchiveWriter
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Stream> WriteAsync(
        SiteId siteId,
        VisitorId visitorId,
        IReadOnlyList<ConversationId> conversationIds,
        string scope,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ago-chat-person-export-{Guid.NewGuid():N}.zip");

        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);

            var conversationIdValues = conversationIds.Select(c => c.Value).ToArray();

            await WriteManifestAsync(archive, siteId, visitorId, conversationIdValues, scope, exportedAt, cancellationToken);
            await WriteVisitorAsync(archive, connection, siteId, visitorId, cancellationToken);
            await WriteChannelIdentitiesAsync(archive, connection, siteId, visitorId, cancellationToken);
            await WriteContactDetailsAsync(archive, connection, visitorId, cancellationToken);
            await WriteConversationsAsync(archive, connection, siteId, conversationIdValues, cancellationToken);
            await WriteMessagesAsync(archive, connection, siteId, conversationIdValues, cancellationToken);
            await WriteAttachmentsAsync(archive, connection, siteId, conversationIdValues, exportedAt, cancellationToken);
        }

        // The one reader is this method's own caller, which streams it straight into the HTTP response
        // and disposes it when done - DeleteOnClose is what removes the temp file at that point, no
        // separate cleanup step to forget (IPersonExportArchiveWriter's own remarks on why this differs
        // from SiteExportJob's manual finally-block delete).
        return new FileStream(
            tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    private static async Task WriteManifestAsync(
        ZipArchive archive, SiteId siteId, VisitorId visitorId, Guid[] conversationIds, string scope,
        DateTimeOffset exportedAt, CancellationToken cancellationToken)
    {
        var manifest = new ManifestDocument(
            FormatVersion,
            siteId.Value,
            scope,
            visitorId.Value,
            conversationIds,
            exportedAt,
            AttachmentBytes: "referenced-by-url",
            Stores: ["visitor", "channelIdentities", "contactDetails", "conversations", "messages", "attachments"],
            ExcludedStores:
            [
                new ExcludedStoreNote(
                    "operators",
                    "the tenant's operator roster is a different person's data - excluding it is this item's own disclosure-boundary rule"),
                new ExcludedStoreNote(
                    "notes",
                    "an operator's private annotation about this visitor - structurally unreachable from visitor-facing paths since `18-04`"),
                new ExcludedStoreNote("tags", "tenant workflow labels, not personal data about this visitor"),
                new ExcludedStoreNote("conversationTags", "the association rows for the excluded `tags` vocabulary above"),
                new ExcludedStoreNote("site", "tenant configuration, not personal data about this visitor"),
            ]);

        var entry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, manifest, JsonOptions, cancellationToken);
    }

    private static async Task WriteVisitorAsync(
        ZipArchive archive, NpgsqlConnection connection, SiteId siteId, VisitorId visitorId, CancellationToken cancellationToken)
    {
        const string sql = "select id, first_seen_at, last_seen_at from visitors where id = @visitorId and site_id = @siteId";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("visitorId", visitorId.Value);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        VisitorExportRow? row = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                row = new VisitorExportRow(
                    reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2));
            }
        }

        var entry = archive.CreateEntry("visitor.json", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, row, JsonOptions, cancellationToken);
    }

    private static async Task WriteChannelIdentitiesAsync(
        ZipArchive archive, NpgsqlConnection connection, SiteId siteId, VisitorId visitorId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, kind, external_address, first_seen_at, last_seen_at, active, unlinked_at
            from channel_identities
            where visitor_id = @visitorId and site_id = @siteId
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("visitorId", visitorId.Value);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        var entry = archive.CreateEntry("channel_identities.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new ChannelIdentityExportRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    /// <summary>`14-14`: no `site_id` column on this table at all (`VisitorContactDetailConfiguration`'s
    /// own remarks) - tenant scope was already checked one level up, by the handler resolving
    /// <paramref name="visitorId"/> from a conversation it proved belongs to the caller's own site.</summary>
    private static async Task WriteContactDetailsAsync(
        ZipArchive archive, NpgsqlConnection connection, VisitorId visitorId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, kind, value, recorded_by_operator_id, recorded_at
            from visitor_contact_details
            where visitor_id = @visitorId
            order by recorded_at
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("visitorId", visitorId.Value);

        var entry = archive.CreateEntry("contact_details.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new ContactDetailExportRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private static async Task WriteConversationsAsync(
        ZipArchive archive, NpgsqlConnection connection, SiteId siteId, Guid[] conversationIds, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, operator_id, state, created_at, last_sequence, operator_unread_count, visitor_unread_count
            from conversations
            where site_id = @siteId and id = any(@conversationIds)
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("conversationIds", conversationIds);

        var entry = archive.CreateEntry("conversations.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new ConversationExportRow(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    /// <summary>`15-09`/`adr/0087`: filters `site_id` directly (the messages partition key), not a
    /// join through `conversations` - the same pruning reasoning `SiteExportArchiveWriter`'s own
    /// remarks give for its own identical choice.</summary>
    private static async Task WriteMessagesAsync(
        ZipArchive archive, NpgsqlConnection connection, SiteId siteId, Guid[] conversationIds, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, conversation_id, sequence, author_id, author_kind, created_at,
                   body, content_kind, content, actions, attachment_id
            from messages
            where site_id = @siteId and conversation_id = any(@conversationIds)
            order by conversation_id, sequence
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("conversationIds", conversationIds);

        var entry = archive.CreateEntry("messages.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // content/actions exported as opaque strings, never parsed - the same rule
            // SiteExportArchiveWriter's own remarks state for these two columns (adr/0061).
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
        ZipArchive archive, NpgsqlConnection connection, SiteId siteId, Guid[] conversationIds, DateTimeOffset exportedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, conversation_id, message_id, object_key, thumbnail_key, content_type, size_bytes, state, created_at
            from attachments
            where site_id = @siteId and conversation_id = any(@conversationIds)
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        command.Parameters.AddWithValue("conversationIds", conversationIds);

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
        int FormatVersion, Guid SiteId, string Scope, Guid VisitorId, IReadOnlyList<Guid> ConversationIds,
        DateTimeOffset ExportedAt, string AttachmentBytes, IReadOnlyList<string> Stores,
        IReadOnlyList<ExcludedStoreNote> ExcludedStores);

    private sealed record ExcludedStoreNote(string Store, string Reason);

    private sealed record VisitorExportRow(Guid Id, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

    private sealed record ChannelIdentityExportRow(
        Guid Id, string Kind, string ExternalAddress, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt,
        bool Active, DateTimeOffset? UnlinkedAt);

    private sealed record ContactDetailExportRow(
        Guid Id, string Kind, string Value, Guid RecordedByOperatorId, DateTimeOffset RecordedAt);

    private sealed record ConversationExportRow(
        Guid Id, Guid? OperatorId, string State, DateTimeOffset CreatedAt, int LastSequence,
        int OperatorUnreadCount, int VisitorUnreadCount);

    private sealed record MessageExportRow(
        Guid Id, Guid ConversationId, int Sequence, Guid AuthorId, string AuthorKind, DateTimeOffset CreatedAt,
        string Body, string? ContentKind, string? Content, string? Actions, Guid? AttachmentId);

    private sealed record AttachmentExportRow(
        Guid Id, Guid ConversationId, Guid? MessageId, string ContentType, long SizeBytes, string State,
        DateTimeOffset CreatedAt, Uri DownloadUrl, Uri? ThumbnailDownloadUrl, DateTimeOffset DownloadUrlExpiresAt);
}
