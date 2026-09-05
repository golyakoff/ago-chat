using System.IO.Compression;
using System.Text.Json;
using Ago.Chat.Application.UseCases.ExportConversation;
using Ago.Chat.Application.UseCases.ExportVisitor;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Dapper;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `24-11`'s own Done-when, end to end against a real Postgres: a conversation-scoped export contains
/// that conversation and nothing else, and a visitor-scoped export spans every conversation the same
/// visitor has and no other visitor's - proven by seeding a second conversation (a different visitor,
/// same site) and asserting its data is absent from either archive, not merely that a file exists.
///
/// <para><b>Fails-before proof</b>: with the writer's own `conversation_id = any(@conversationIds)`
/// predicates commented out (both call sites in <c>PersonExportArchiveWriter</c>), both tests below
/// fail - <c>ExportingAConversation_...</c> because the stranger conversation's own message body
/// appears in <c>messages.jsonl</c>, and <c>ExportingAVisitor_...</c> for the identical reason. Restoring
/// the predicates makes both pass again. This is the mandatory proof CLAUDE.md's testing discipline
/// asks for: a test that would have caught the gap `24-11` exists to close, shown actually catching
/// it.</para>
/// </summary>
[Collection(AttachmentCollection.Name)]
public class PersonExportIntegrationTests(AttachmentFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 9, 0, 0, TimeSpan.Zero);

    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public async Task ExportingAConversation_ProducesAnArchive_ContainingOnlyThatConversation_NotASecondOne()
    {
        var (siteId, _, _) = await SeedSiteAsync("person-export-conv");
        var operatorId = await SeedOperatorAsync(siteId, Permission.ConversationExport);

        var (visitorId, conversationId, _) = await SeedConversationAsync(siteId, "mine: hello", "mine: anyone there");
        await SeedChannelIdentityAsync(siteId, visitorId, "+70000000001");
        await SeedContactDetailAsync(visitorId, operatorId, "mine@example.invalid");
        var (attachmentBytes, _) = await SeedAttachmentAsync(siteId, conversationId);

        // A second, unrelated conversation on the same site - a different visitor entirely. Its own
        // message body and channel address must never appear in the first conversation's export.
        var (strangerVisitorId, strangerConversationId, _) =
            await SeedConversationAsync(siteId, "stranger: private", "stranger: secret");
        await SeedChannelIdentityAsync(siteId, strangerVisitorId, "+79999999999");
        await SeedContactDetailAsync(strangerVisitorId, operatorId, "stranger@example.invalid");
        await SeedAttachmentAsync(siteId, strangerConversationId);

        var handler = CreateExportConversationHandler();
        var result = await handler.HandleAsync(
            new Application.UseCases.ExportConversation.ExportConversation(conversationId, operatorId, siteId), CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.ToString() : null);

        using var archive = await ReadArchiveAsync(result.Value.Content);

        var manifest = await ReadJsonAsync(archive, "manifest.json");
        Assert.Equal(1, manifest.GetProperty("formatVersion").GetInt32());
        Assert.Equal("conversation", manifest.GetProperty("scope").GetString());
        Assert.Equal(siteId.Value, manifest.GetProperty("siteId").GetGuid());
        var excluded = manifest.GetProperty("excludedStores").EnumerateArray().Select(e => e.GetProperty("store").GetString()).ToList();
        Assert.Contains("operators", excluded);
        Assert.Contains("notes", excluded);
        Assert.Contains("tags", excluded);

        var conversationRows = await ReadJsonLinesAsync(archive, "conversations.jsonl");
        Assert.Equal(conversationId.Value, Assert.Single(conversationRows).GetProperty("id").GetGuid());

        var messageRows = await ReadJsonLinesAsync(archive, "messages.jsonl");
        var bodies = messageRows.Select(m => m.GetProperty("body").GetString()).ToList();
        Assert.Contains("mine: hello", bodies);
        Assert.Contains("mine: anyone there", bodies);
        Assert.DoesNotContain("stranger: private", bodies);
        Assert.DoesNotContain("stranger: secret", bodies);
        Assert.All(messageRows, m => Assert.Equal(conversationId.Value, m.GetProperty("conversationId").GetGuid()));

        var channelRows = await ReadJsonLinesAsync(archive, "channel_identities.jsonl");
        var addresses = channelRows.Select(c => c.GetProperty("externalAddress").GetString()).ToList();
        Assert.Contains("+70000000001", addresses);
        Assert.DoesNotContain("+79999999999", addresses);

        var contactRows = await ReadJsonLinesAsync(archive, "contact_details.jsonl");
        var contactValues = contactRows.Select(c => c.GetProperty("value").GetString()).ToList();
        Assert.Contains("mine@example.invalid", contactValues);
        Assert.DoesNotContain("stranger@example.invalid", contactValues);

        var attachmentRows = await ReadJsonLinesAsync(archive, "attachments.jsonl");
        Assert.Single(attachmentRows);
        Assert.All(attachmentRows, a => Assert.Equal(conversationId.Value, a.GetProperty("conversationId").GetGuid()));
        _ = attachmentBytes;
    }

    [Fact]
    public async Task ExportingAVisitor_ProducesAnArchive_SpanningAllTheirConversations_ButNotAnotherVisitors()
    {
        var (siteId, _, _) = await SeedSiteAsync("person-export-visitor");
        var operatorId = await SeedOperatorAsync(siteId, Permission.ConversationExport);

        var (visitorId, firstConversationId, _) = await SeedConversationAsync(siteId, "first: hi", "first: bye");
        var secondConversationId = await SeedSecondConversationForVisitorAsync(siteId, visitorId, "second: back again");

        var (strangerVisitorId, strangerConversationId, _) =
            await SeedConversationAsync(siteId, "stranger: hi", "stranger: bye");
        _ = strangerVisitorId;

        var handler = CreateExportVisitorHandler();
        var result = await handler.HandleAsync(
            new Application.UseCases.ExportVisitor.ExportVisitor(firstConversationId, operatorId, siteId), CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.ToString() : null);

        using var archive = await ReadArchiveAsync(result.Value.Content);

        var manifest = await ReadJsonAsync(archive, "manifest.json");
        Assert.Equal("visitor", manifest.GetProperty("scope").GetString());
        Assert.Equal(visitorId.Value, manifest.GetProperty("visitorId").GetGuid());

        var conversationRows = await ReadJsonLinesAsync(archive, "conversations.jsonl");
        var conversationIds = conversationRows.Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(firstConversationId.Value, conversationIds);
        Assert.Contains(secondConversationId.Value, conversationIds);
        Assert.DoesNotContain(strangerConversationId.Value, conversationIds);
        Assert.Equal(2, conversationIds.Count);

        var messageRows = await ReadJsonLinesAsync(archive, "messages.jsonl");
        var bodies = messageRows.Select(m => m.GetProperty("body").GetString()).ToList();
        Assert.Contains("first: hi", bodies);
        Assert.Contains("second: back again", bodies);
        Assert.DoesNotContain("stranger: hi", bodies);
        Assert.DoesNotContain("stranger: bye", bodies);
    }

    private ExportConversationHandler CreateExportConversationHandler()
    {
        var readStore = new ConversationReadStore(fixture.DataSource);
        var writer = new PersonExportArchiveWriter(fixture.DataSource, fixture.FileStorage, new PersonExportOptions());
        var db = fixture.CreateDbContext();
        return new ExportConversationHandler(
            readStore, writer, new FakeRateLimiter(), new PermissionChecker(db), new PersonExportRateLimitOptions(),
            new SettableClock(Now));
    }

    private ExportVisitorHandler CreateExportVisitorHandler()
    {
        var readStore = new ConversationReadStore(fixture.DataSource);
        var writer = new PersonExportArchiveWriter(fixture.DataSource, fixture.FileStorage, new PersonExportOptions());
        var db = fixture.CreateDbContext();
        return new ExportVisitorHandler(
            readStore, writer, new FakeRateLimiter(), new PermissionChecker(db), new PersonExportRateLimitOptions(),
            new SettableClock(Now));
    }

    private async Task<(SiteId SiteId, string Name, string AllowedOrigin)> SeedSiteAsync(string name)
    {
        var siteId = new SiteId(Guid.NewGuid());
        const string origin = "https://shop.example";
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [origin], name));
        await db.SaveChangesAsync();
        return (siteId, name, origin);
    }

    private async Task<OperatorId> SeedOperatorAsync(SiteId siteId, Permission permission)
    {
        var operatorId = new OperatorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Operators.Add(new Operator(
            operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: $"subject-{operatorId.Value:N}"));
        var roleId = Guid.NewGuid();
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [permission.Value] });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        await db.SaveChangesAsync();
        return operatorId;
    }

    private async Task<(VisitorId VisitorId, ConversationId ConversationId, IReadOnlyList<Guid> MessageIds)> SeedConversationAsync(
        SiteId siteId, string firstBody, string secondBody)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        var firstMessageId = Guid.NewGuid();
        var secondMessageId = Guid.NewGuid();
        conversation.AddVisitorMessage(visitorId, new MessageId(firstMessageId), new MessageBody(firstBody), Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(secondMessageId), new MessageBody(secondBody), Now.AddSeconds(1));

        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            await db.SaveChangesAsync();
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        return (visitorId, conversation.Id, [firstMessageId, secondMessageId]);
    }

    private async Task<ConversationId> SeedSecondConversationForVisitorAsync(SiteId siteId, VisitorId visitorId, string body)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now.AddMinutes(10));
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody(body), Now.AddMinutes(10));

        await using var db = fixture.CreateDbContext();
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return conversation.Id;
    }

    private async Task SeedChannelIdentityAsync(SiteId siteId, VisitorId visitorId, string address)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into channel_identities (id, site_id, visitor_id, kind, external_address, first_seen_at, last_seen_at, active)
            values (@id, @siteId, @visitorId, 'Sms', @address, @now, @now, true)
            """,
            new { id = Guid.NewGuid(), siteId = siteId.Value, visitorId = visitorId.Value, address, now = Now });
    }

    private async Task SeedContactDetailAsync(VisitorId visitorId, OperatorId operatorId, string value)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into visitor_contact_details (id, visitor_id, kind, value, recorded_by_operator_id, recorded_at)
            values (@id, @visitorId, 'Phone', @value, @operatorId, @now)
            """,
            new { id = Guid.NewGuid(), visitorId = visitorId.Value, value, operatorId = operatorId.Value, now = Now });
    }

    private static readonly HttpClient Http = new();

    private async Task<(byte[] Bytes, string ObjectKey)> SeedAttachmentAsync(SiteId siteId, ConversationId conversationId)
    {
        byte[] bytes = [9, 8, 7];
        var objectKey = $"site/{siteId.Value}/conv/{conversationId.Value}/{Guid.NewGuid():N}.png";

        var presigned = await fixture.FileStorage.CreateUploadAsync(
            new ObjectKey(objectKey),
            new UploadConstraints("image/png", bytes.Length, TimeSpan.FromMinutes(5)),
            CancellationToken.None);
        using (var content = new ByteArrayContent(bytes))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            using var response = await Http.PutAsync(presigned.Url, content);
            response.EnsureSuccessStatusCode();
        }

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into attachments (id, site_id, conversation_id, object_key, content_type, size_bytes, state, created_at)
            values (@id, @siteId, @conversationId, @objectKey, 'image/png', @sizeBytes, 'Ready', @now)
            """,
            new
            {
                id = Guid.NewGuid(),
                siteId = siteId.Value,
                conversationId = conversationId.Value,
                objectKey,
                sizeBytes = (long)bytes.Length,
                now = Now,
            });

        return (bytes, objectKey);
    }

    private static async Task<ZipArchive> ReadArchiveAsync(Stream content)
    {
        var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        await content.DisposeAsync();
        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    private static async Task<JsonElement> ReadJsonAsync(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Archive has no entry '{entryName}'.");
        await using var stream = entry.Open();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadJsonLinesAsync(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Archive has no entry '{entryName}'.");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var lines = new List<JsonElement>();
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            lines.Add(JsonDocument.Parse(line).RootElement.Clone());
        }

        return lines;
    }
}
