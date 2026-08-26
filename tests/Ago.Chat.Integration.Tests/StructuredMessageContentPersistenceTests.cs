using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-06`'s first Done-when, against a real Postgres: a message with a kind, a payload and actions
/// persists, comes back through the aggregate <i>and</i> through the Dapper read model, and reaches
/// the wire DTO the hubs and the fan-out path both build.
///
/// <para>Both read paths are asserted because they are genuinely different code - EF with value
/// converters on one side, hand-written SQL and Dapper's constructor binding on the other - and
/// `5-11`'s own finding was a field that arrived correctly through one and as <c>undefined</c>
/// through the other.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class StructuredMessageContentPersistenceTests(PostgresFixture fixture)
{
    // Real time, not a fixed date - 2-06 partitions messages by created_at, and only the current
    // month plus the next two ever have a partition.
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private const string Payload = """{"title":"Hold ready","reference":"H-4417","branches":["cen","riv"]}""";

    [Fact]
    public async Task AStructuredMessageRoundTripsThroughTheAggregate()
    {
        var (conversationId, _) = await SeedAsync(WithContent());

        await using var db = fixture.CreateDbContext();
        var conversation = await db.Conversations
            .Include("_messages")
            .SingleAsync(c => c.Id == conversationId);

        var message = conversation.Messages.Single();

        Assert.NotNull(message.Content);
        Assert.Equal("holds.pickup_choice", message.Content!.Kind.Value);

        // Byte-for-byte, keys in the producer's own order. This is what `text` buys over `jsonb`, and
        // it is the difference a product that signs its payloads would notice.
        Assert.Equal(Payload, message.Content.Payload!.Value.Value);

        Assert.Equal(
            [("Central Library", "cen"), ("Riverside Branch", "riv")],
            message.Content.Actions.Select(a => (a.Label, a.Value)));

        // The body is still there and still required - the rule that makes a text-only channel work.
        Assert.Equal("Your hold is ready. Which branch?", message.Body.Value);
    }

    [Fact]
    public async Task AStructuredMessageRoundTripsThroughTheDapperReadModelAndOntoTheWire()
    {
        var (conversationId, _) = await SeedAsync(WithContent());

        var page = await new ConversationReadStore(fixture.DataSource)
            .GetHistoryAsync(conversationId, beforeSequence: null, pageSize: 10, CancellationToken.None);

        var item = page.Messages.Single();
        Assert.Equal("holds.pickup_choice", item.ContentKind);
        Assert.Equal(Payload, item.Payload);

        // And through the one mapper all three delivery paths share, so the fan-out copy and the
        // local echo cannot disagree.
        var dto = MessageDtoMapper.ToDto(item, conversationId);

        Assert.Equal("holds.pickup_choice", dto.ContentKind);
        Assert.Equal("Hold ready", dto.Content!.Value.GetProperty("title").GetString());
        Assert.Equal(
            ["Central Library", "Riverside Branch"],
            dto.Actions!.Select(a => a.Label));
    }

    [Fact]
    public async Task AProseMessageLeavesAllThreeColumnsNull()
    {
        // The overwhelmingly common case, and the one the storage decision was argued around: three
        // NULLs, which Postgres records in a null bitmap the row already had.
        var (conversationId, _) = await SeedAsync(content: null);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select content_kind is null and content is null and actions is null
            from messages where conversation_id = @c
            """,
            connection);
        command.Parameters.AddWithValue("c", conversationId.Value);

        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task AMessageWithActionsAndNoPayload_StoresNullContentAndStillReadsBack()
    {
        // The shape a text channel produces natively: a prompt and its choices, nothing to draw.
        var content = MessageContent.Create(
            new MessageContentKind("holds.pickup_choice"),
            payload: null,
            actions: [new MessageAction("Central Library", "cen")]);

        var (conversationId, _) = await SeedAsync(content);

        var page = await new ConversationReadStore(fixture.DataSource)
            .GetHistoryAsync(conversationId, beforeSequence: null, pageSize: 10, CancellationToken.None);

        var item = page.Messages.Single();
        Assert.Equal("holds.pickup_choice", item.ContentKind);
        Assert.Null(item.Payload);

        var dto = MessageDtoMapper.ToDto(item, conversationId);
        Assert.Null(dto.Content);
        Assert.Single(dto.Actions!);
    }

    [Fact]
    public async Task ThePayloadCeilingIsAStorageConstraintToo_NotOnlyADomainCheck()
    {
        // The domain refuses an oversized payload, and MessagePayload's own unit test proves that.
        // This proves the other half: the column carries a CHECK, so a writer that somehow reached
        // the table without going through the domain is refused as well. data-model.md's own rule -
        // "anything enforcing a guarantee gets a constraint, not just application code" - applied to
        // the one opaque field on the one write path that accepts unauthenticated input.
        var (conversationId, _) = await SeedAsync(content: null);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "update messages set content = @c where conversation_id = @id", connection);
        command.Parameters.AddWithValue("c", "{\"a\":\"" + new string('x', MessagePayload.MaxLength) + "\"}");
        command.Parameters.AddWithValue("id", conversationId.Value);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal("ck_messages_content_length", failure.ConstraintName);
    }

    [Fact]
    public async Task APayloadExactlyAtTheCeilingIsAccepted_SoTheConstraintAndTheDomainAgree()
    {
        // The two limits are stated in two places (MessagePayload.MaxLength and the migration's
        // CHECK), which is a drift hazard accepted deliberately. This is what notices the drift: a
        // payload the domain accepts must not be refused by the column.
        var atTheLimit = "{\"a\":\"" + new string('x', MessagePayload.MaxLength - 8) + "\"}";
        Assert.Equal(MessagePayload.MaxLength, atTheLimit.Length);

        var content = MessageContent.Create(
            new MessageContentKind("holds.pickup_choice"), new MessagePayload(atTheLimit));

        var (conversationId, _) = await SeedAsync(content);

        var page = await new ConversationReadStore(fixture.DataSource)
            .GetHistoryAsync(conversationId, beforeSequence: null, pageSize: 10, CancellationToken.None);

        Assert.Equal(atTheLimit, page.Messages.Single().Payload);
    }

    private static MessageContent WithContent() => MessageContent.Create(
        new MessageContentKind("holds.pickup_choice"),
        new MessagePayload(Payload),
        [
            new MessageAction("Central Library", "cen"),
            new MessageAction("Riverside Branch", "riv"),
        ]);

    private async Task<(ConversationId ConversationId, MessageId MessageId)> SeedAsync(MessageContent? content)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        var messageId = new MessageId(Guid.NewGuid());

        conversation.AddVisitorMessage(
            visitorId, messageId, new MessageBody("Your hold is ready. Which branch?"), Now, content: content);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (conversation.Id, messageId);
    }
}
