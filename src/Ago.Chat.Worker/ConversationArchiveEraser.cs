using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Worker;

/// <summary>
/// `24-09`: the piece `ConversationErasureJob`'s own remarks used to say did not exist because nothing
/// archived yet - `13-06` shipped the archive since, and this is what makes an erased conversation's
/// messages actually disappear from it too, not just from the live `messages` table.
///
/// <para><b>Rewrite, not whole-object delete - `docs/adr/0108-*`.</b> One archive object
/// (<c>archive/messages/{siteId}/{class}/{period}.zip</c>) covers every conversation one site had in
/// one retention class for one month, not one conversation - <see cref="MessageArchiveWriter"/>'s own
/// query is a `(site_id, retention_class, period)` scan, never scoped to a conversation. Deleting the
/// whole object to erase one conversation would destroy every other visitor's transcript archived
/// alongside it in the same period; this class instead downloads the object, drops only the
/// `messages.jsonl`/`attachments.jsonl` lines naming the erased conversation, and re-uploads the result
/// to the same key - a read-modify-write, not a delete.</para>
///
/// <para><b>No widening of <see cref="IFileStorage"/>.</b> That port's own doc comment is explicit that
/// every method "issues a short-lived presigned URL a client uses directly against storage... never
/// streams a byte itself" - <see cref="MessageArchiveJob.ArchiveOneAsync"/> already established the
/// pattern this class reuses for the write half (presign a PUT, then `HttpClient.PutAsync` the bytes
/// directly against it); this class adds the read half the identical way (presign a GET, then
/// `HttpClient.GetAsync` against it). Both hops happen in `Ago.Chat.Worker`, never in `Ago.Chat.Api` -
/// `CLAUDE.md`'s "bytes never pass through the API" is about the end-user-facing attachment path
/// specifically, and this is a rare, background, administrative rewrite of a small text object, not
/// that path.</para>
///
/// <para><b>Every archived period is checked, not only ones the conversation's still-live messages
/// point at.</b> A conversation whose oldest messages already aged past their retention window has
/// nothing left in `messages` to say which periods to look at - the partition that held them was
/// already dropped, per `adr/0031`'s own archive-then-drop ordering. So the only reliable source of
/// "which periods might mention this conversation" is the site's own archive manifest
/// (<see cref="IMessageArchiveRepository.ListForSiteAsync"/>), and every one of its rows is opened.
/// Bounded by how many (retention class, month) periods the site has ever had archived - the same small,
/// slowly-growing count <see cref="MessageArchiveJob"/> itself iterates - not by anything proportional
/// to this one conversation's size. A period that turns out not to mention the conversation at all is
/// left untouched (no re-upload), which is what keeps the common case cheap.</para>
///
/// <para><b>This is the new way conversation erasure can fail.</b> Before this class existed, erasure
/// depended only on Postgres and MinIO's <c>DELETE</c>/presigned-PUT paths it already used for
/// attachments. A download or upload here that fails (storage unavailable, a truncated object) is
/// allowed to throw rather than being logged and tolerated the way an attachment-object delete is -
/// silently finishing the conversation's erasure while an archived copy still stands would be exactly
/// the bug this item exists to close, so a failure here must leave the conversation flagged for retry,
/// not report success. The one exception is a `404` on the read: an archive row whose object is already
/// gone is treated as "nothing left to remove", the same "delete of an already-deleted object is a
/// no-op" idempotency the rest of this codebase's retention jobs rely on.</para>
/// </summary>
public sealed class ConversationArchiveEraser(
    IFileStorage fileStorage,
    IMessageArchiveRepository archives,
    ConversationErasureJobOptions options,
    ILogger<ConversationArchiveEraser> logger)
{
    // A Worker-internal transfer against a presigned URL, the identical "one static HttpClient, no
    // per-call disposal" shape MessageArchiveJob's own upload half already uses.
    private static readonly HttpClient Http = new();

    /// <summary>Every archived period this site has, checked for this conversation's own rows. Returns
    /// the total number of archived message lines removed, purely for the caller to log - not a claim
    /// that this is the only durable evidence an erasure happened (`24-13` is the separate, still-open
    /// gap for a receipt).</summary>
    public async Task<int> EraseAsync(SiteId siteId, Guid conversationId, CancellationToken cancellationToken)
    {
        var records = await archives.ListForSiteAsync(siteId, cancellationToken);

        var removed = 0;
        foreach (var record in records)
        {
            removed += await EraseFromOneArchiveAsync(record, conversationId, cancellationToken);
        }

        return removed;
    }

    private async Task<int> EraseFromOneArchiveAsync(
        MessageArchiveRecord record, Guid conversationId, CancellationToken cancellationToken)
    {
        var downloadUrl = await fileStorage.CreateDownloadUrlAsync(
            new ObjectKey(record.ObjectKey), options.ArchiveDownloadUrlLifetime, cancellationToken);

        var downloadedPath = Path.Combine(Path.GetTempPath(), $"ago-chat-archive-erasure-read-{Guid.NewGuid():N}.zip");
        var downloaded = await TryDownloadAsync(downloadUrl, downloadedPath, cancellationToken);
        if (!downloaded)
        {
            // 404: the manifest row still names this object, but it is already gone (e.g. deleted by
            // hand, or by a future retention step on the archive itself). Nothing to remove from
            // something that is not there - the same "already gone counts as done" idempotency
            // ConversationErasureQuery's own attachment-object delete relies on.
            logger.LogWarning(
                "Archive object {ObjectKey} named by message_archives no longer exists in storage; nothing to erase from it.",
                record.ObjectKey);
            return 0;
        }

        try
        {
            string manifestJson;
            var keptMessageLines = new List<string>();
            var keptAttachmentLines = new List<string>();
            var removedCount = 0;

            using (var archive = ZipFile.OpenRead(downloadedPath))
            {
                manifestJson = await ReadEntryTextAsync(archive, "manifest.json", cancellationToken);

                foreach (var line in await ReadEntryLinesAsync(archive, "messages.jsonl", cancellationToken))
                {
                    if (LineBelongsToConversation(line, conversationId))
                    {
                        removedCount++;
                    }
                    else
                    {
                        keptMessageLines.Add(line);
                    }
                }

                foreach (var line in await ReadEntryLinesAsync(archive, "attachments.jsonl", cancellationToken))
                {
                    if (!LineBelongsToConversation(line, conversationId))
                    {
                        keptAttachmentLines.Add(line);
                    }
                }
            }

            if (removedCount == 0)
            {
                // This conversation never touched this period - leave the object exactly as it was.
                // Skipping the rewrite for the common case is what keeps the per-conversation cost
                // proportional to "how many periods actually mention it", not to "how many periods the
                // site has ever had archived".
                return 0;
            }

            await RewriteAndUploadAsync(record.ObjectKey, manifestJson, keptMessageLines, keptAttachmentLines, cancellationToken);

            logger.LogInformation(
                "Removed {Count} archived message(s) for conversation {ConversationId} from archive {ObjectKey}.",
                removedCount, conversationId, record.ObjectKey);

            return removedCount;
        }
        finally
        {
            TryDelete(downloadedPath);
        }
    }

    private static bool LineBelongsToConversation(string jsonLine, Guid conversationId)
    {
        using var document = JsonDocument.Parse(jsonLine);
        return document.RootElement.GetProperty("conversationId").GetGuid() == conversationId;
    }

    private static async Task<string> ReadEntryTextAsync(ZipArchive archive, string entryName, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"Archive is missing its own '{entryName}' entry.");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task<List<string>> ReadEntryLinesAsync(ZipArchive archive, string entryName, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"Archive is missing its own '{entryName}' entry.");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private async Task<bool> TryDownloadAsync(Uri url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await contentStream.CopyToAsync(fileStream, cancellationToken);
        return true;
    }

    private async Task RewriteAndUploadAsync(
        string objectKey, string manifestJson, IReadOnlyList<string> messageLines, IReadOnlyList<string> attachmentLines,
        CancellationToken cancellationToken)
    {
        var rewrittenPath = Path.Combine(Path.GetTempPath(), $"ago-chat-archive-erasure-write-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fileStream = new FileStream(rewrittenPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);
                await WriteTextEntryAsync(archive, "manifest.json", manifestJson, cancellationToken);
                await WriteJsonLinesEntryAsync(archive, "messages.jsonl", messageLines, cancellationToken);
                await WriteJsonLinesEntryAsync(archive, "attachments.jsonl", attachmentLines, cancellationToken);
            }

            var length = new FileInfo(rewrittenPath).Length;
            var upload = await fileStorage.CreateUploadAsync(
                new ObjectKey(objectKey), new UploadConstraints("application/zip", length, options.ArchiveUploadUrlLifetime),
                cancellationToken);

            await using var uploadStream = new FileStream(rewrittenPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var content = new StreamContent(uploadStream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            content.Headers.ContentLength = length;
            using var response = await Http.PutAsync(upload.Url, content, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            TryDelete(rewrittenPath);
        }
    }

    private static async Task WriteTextEntryAsync(ZipArchive archive, string entryName, string text, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }

    private static async Task WriteJsonLinesEntryAsync(
        ZipArchive archive, string entryName, IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);
        foreach (var line in lines)
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete archive-erasure temp file {Path}.", path);
        }
    }
}
