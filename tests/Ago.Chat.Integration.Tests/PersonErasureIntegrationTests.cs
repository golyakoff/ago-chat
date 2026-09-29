using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ErasePerson;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `adr/0189`/`26-275` slice #3's own Done-when: "consuming `PersonErased` erases the whole chat side of
/// the person." The calendar's own half (`PersonRecord`, past events) is already erased by the time this
/// event arrives (`adr/0189`); this suite proves chat's own cascade - the `Visitor`/Person row, every one
/// of their conversations and everything under them (messages, contact details, person notes) - completes
/// idempotently, and that an unknown person is a clean no-op, the same completeness claim
/// `ConversationErasureIntegrationTests` proves one aggregate down. Reuses <see cref="ErasureFixture"/>
/// (real Postgres + MinIO) for the identical reason that file does: <see cref="ConversationErasureJob"/>
/// is what actually drains a person's conversations, and this suite exercises it for real rather than
/// re-deriving its own behaviour with a double.
/// </summary>
[Collection(ErasureCollection.Name)]
public class PersonErasureIntegrationTests(ErasureFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public async Task PersonErased_ErasesThePersonAndBothTheirConversations_OnceTheConversationsHaveDrained()
    {
        var clock = new SettableClock(Now);
        var siteId = await SeedSiteAsync("person-erasure-site");
        var operatorId = await SeedOperatorAsync(siteId);

        var (firstConversationId, personId) = await SeedConversationAsync(siteId);
        // `ix_conversations_one_open_per_visitor` allows only one *open* conversation per visitor at a
        // time - the first must be closed before a second can exist for the same person, the ordinary
        // "this person came back after their last conversation ended" shape, not a race.
        await CloseConversationAsync(firstConversationId);
        var (secondConversationId, samePersonId) = await SeedConversationAsync(siteId, personId);
        Assert.Equal(personId, samePersonId);

        await SeedPersonNoteAsync(personId, operatorId, "erase-me note about the person");
        await SeedContactDetailAsync(personId, operatorId, "+7 000 000-10-01");

        // Both halves genuinely exist before erasure.
        Assert.Equal(1, await CountAsync("select count(*) from visitors where id = @id", personId.Value));
        Assert.Equal(2, await CountAsync("select count(*) from messages where conversation_id = @id", firstConversationId.Value));
        Assert.Equal(2, await CountAsync("select count(*) from messages where conversation_id = @id", secondConversationId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from person_notes where visitor_id = @id", personId.Value));
        Assert.Equal(1, await CountAsync("select count(*) from visitor_contact_details where visitor_id = @id", personId.Value));

        var erasePersonOutcome = await ErasePersonAsync(siteId, personId, clock.UtcNow);
        Assert.Equal(PersonErasureOutcome.Requested, erasePersonOutcome);

        var personJob = CreatePersonJob(clock);
        var conversationJob = CreateConversationJob(clock);

        // Tick 1: the person job stamps both conversations and finds they still exist - not yet erased.
        Assert.Equal(0, await personJob.SweepAsync(CancellationToken.None));
        Assert.Equal(1, await CountAsync("select count(*) from visitors where id = @id", personId.Value));
        Assert.Equal(
            2,
            await CountAsync(
                "select count(*) from conversations where visitor_id = @id and erasure_requested_at is not null",
                personId.Value));

        // ConversationErasureJob drains both conversations, off its own independent ticks.
        for (var i = 0; i < 3; i++)
        {
            await conversationJob.SweepAsync(CancellationToken.None);
        }

        Assert.Equal(0, await CountAsync("select count(*) from conversations where id = @id", firstConversationId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from conversations where id = @id", secondConversationId.Value));

        // Tick 2: no conversation remains for this person - the person job now removes the Visitor row.
        Assert.Equal(1, await personJob.SweepAsync(CancellationToken.None));

        Assert.Equal(0, await CountAsync("select count(*) from visitors where id = @id", personId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from person_notes where visitor_id = @id", personId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from visitor_contact_details where visitor_id = @id", personId.Value));

        // The site and the operator: completely untouched.
        Assert.Equal(1, await CountAsync("select count(*) from sites where id = @id", siteId.Value));
    }

    /// <summary>`26-268`'s own manual client: a Person who was registered from a booking form but never
    /// opened a conversation at all. The person job's very first tick clears the "no conversation" gate
    /// immediately, so this person is fully erased in one pass - the cascade relies entirely on the
    /// schema's own `ON DELETE CASCADE` from `visitors` for their contact detail, since no conversation
    /// ever drained it explicitly.</summary>
    [Fact]
    public async Task PersonErased_ForAPersonWithNoConversationAtAll_ErasesThemOnTheFirstTick()
    {
        var clock = new SettableClock(Now);
        var siteId = await SeedSiteAsync("person-erasure-no-conversation-site");
        var personId = await SeedPersonWithNoConversationAsync(siteId, "+7 000 000-10-02");

        Assert.Equal(PersonErasureOutcome.Requested, await ErasePersonAsync(siteId, personId, clock.UtcNow));

        var personJob = CreatePersonJob(clock);
        Assert.Equal(1, await personJob.SweepAsync(CancellationToken.None));

        Assert.Equal(0, await CountAsync("select count(*) from visitors where id = @id", personId.Value));
        Assert.Equal(0, await CountAsync("select count(*) from visitor_contact_details where visitor_id = @id", personId.Value));
    }

    /// <summary>CLAUDE.md rule 5: a redelivery of the same `PersonErased` event before the sweep has run
    /// finds the flag already set and changes nothing - not the timestamp, and it does not mint a second
    /// anything, since there is no receipt for a person-scoped erasure to begin with. Drains the person
    /// through <see cref="PersonErasureJob"/> before returning, the same "never leave a flagged row
    /// standing" discipline every other test in this suite follows - <see cref="PersonErasureJob.SweepAsync"/>
    /// claims every pending person account-wide, not only the one this test seeded, so a test that left
    /// its own row flagged forever would silently inflate a later test's own sweep count.</summary>
    [Fact]
    public async Task PersonErased_DeliveredTwiceBeforeTheSweepRuns_IsANoOpTheSecondTime()
    {
        var clock = new SettableClock(Now);
        var siteId = await SeedSiteAsync("person-erasure-redelivery-site");
        var personId = await SeedPersonWithNoConversationAsync(siteId, "+7 000 000-10-03");

        var first = await ErasePersonAsync(siteId, personId, Now);
        var second = await ErasePersonAsync(siteId, personId, Now.AddHours(1));

        Assert.Equal(PersonErasureOutcome.Requested, first);
        Assert.Equal(PersonErasureOutcome.AlreadyRequested, second);
        Assert.Equal(Now, await GetErasureRequestedAtAsync(personId));

        Assert.Equal(1, await CreatePersonJob(clock).SweepAsync(CancellationToken.None));
        Assert.Equal(0, await CountAsync("select count(*) from visitors where id = @id", personId.Value));
    }

    /// <summary>A `PersonErased` naming a person this deployment never registered - or one an earlier
    /// delivery already fully erased - is a clean no-op: nothing is written, and nothing throws.</summary>
    [Fact]
    public async Task PersonErased_ForAnUnknownPerson_WritesNothing_AndSaysSo()
    {
        var siteId = await SeedSiteAsync("person-erasure-unknown-site");
        var neverRegisteredPersonId = new VisitorId(Guid.NewGuid());

        var outcome = await ErasePersonAsync(siteId, neverRegisteredPersonId, Now);

        Assert.Equal(PersonErasureOutcome.UnknownPerson, outcome);
        Assert.Equal(0, await CountAsync("select count(*) from visitors where id = @id", neverRegisteredPersonId.Value));
    }

    /// <summary>The same "cross-tenant existence leak" defence <see cref="IErasureRequestRepository"/>'s
    /// own remarks describe: a person who genuinely exists, but under a different account than the event
    /// names, must be indistinguishable from one that does not exist at all.</summary>
    [Fact]
    public async Task PersonErased_NamingTheWrongAccount_IsAlsoAnUnknownPerson_AndLeavesThePersonUntouched()
    {
        var ownAccount = await SeedSiteAsync("person-erasure-own-account-site");
        var otherAccount = await SeedSiteAsync("person-erasure-other-account-site");
        var personId = await SeedPersonWithNoConversationAsync(ownAccount, "+7 000 000-10-04");

        var outcome = await ErasePersonAsync(otherAccount, personId, Now);

        Assert.Equal(PersonErasureOutcome.UnknownPerson, outcome);
        Assert.Equal(1, await CountAsync("select count(*) from visitors where id = @id", personId.Value));
    }

    private async Task<PersonErasureOutcome> ErasePersonAsync(SiteId accountId, VisitorId personId, DateTimeOffset occurredAt)
    {
        var store = new PersonErasureStore(fixture.DataSource);
        var handler = new ErasePersonHandler(store);
        return await handler.HandleAsync(new Application.UseCases.ErasePerson.ErasePerson(accountId, personId, occurredAt), CancellationToken.None);
    }

    private PersonErasureJob CreatePersonJob(IClock clock) =>
        new(fixture.DataSource, clock, Options.Create(new PersonErasureJobOptions()), NullLogger<PersonErasureJob>.Instance);

    private ConversationErasureJob CreateConversationJob(IClock clock)
    {
        var erasureOptions = new ConversationErasureJobOptions();
        var archiveEraser = new ConversationArchiveEraser(
            fixture.FileStorage, new MessageArchiveRepository(fixture.DataSource), erasureOptions,
            NullLogger<ConversationArchiveEraser>.Instance);
        return new ConversationErasureJob(
            fixture.DataSource, fixture.FileStorage, archiveEraser, clock,
            Options.Create(erasureOptions), NullLogger<ConversationErasureJob>.Instance);
    }

    /// <summary>A conversation with two visitor messages, on either a brand-new person or an existing one
    /// - <paramref name="existingPersonId"/> lets the same person own two conversations at once, the same
    /// "a real chat-origin client can have more than one conversation" shape this suite's own first test
    /// needs to prove the cascade reaches every one of them.</summary>
    private async Task<(ConversationId ConversationId, VisitorId PersonId)> SeedConversationAsync(
        SiteId siteId, VisitorId? existingPersonId = null)
    {
        var personId = existingPersonId ?? new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, personId, Now);
        conversation.AddVisitorMessage(personId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now);
        conversation.AddVisitorMessage(personId, new MessageId(Guid.NewGuid()), new MessageBody("still there?"), Now);

        await using var db = fixture.CreateDbContext();
        if (existingPersonId is null)
        {
            db.Visitors.Add(new Visitor(personId, siteId, Now));
            await db.SaveChangesAsync();
        }

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (conversation.Id, personId);
    }

    /// <summary>Ends a conversation the ordinary way (`Conversation.Close`), through the aggregate's own
    /// load-mutate-save path - not a raw `UPDATE`, since this suite's whole point is proving the real
    /// erasure machinery against rows written the way production actually writes them.</summary>
    private async Task CloseConversationAsync(ConversationId conversationId)
    {
        await using var db = fixture.CreateDbContext();
        var conversation = await db.Conversations.SingleAsync(c => c.Id == conversationId);
        conversation.Close(Now);
        await db.SaveChangesAsync();
    }

    private async Task<VisitorId> SeedPersonWithNoConversationAsync(SiteId siteId, string phone)
    {
        var personId = new VisitorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Visitors.Add(new Visitor(personId, siteId, Now));
            await db.SaveChangesAsync();
        }

        await using var contactDb = fixture.CreateDbContext();
        await new VisitorContactDetailRepository(contactDb).SaveAsync(
            VisitorContactDetail.RecordFromVisitor(new VisitorContactDetailId(Guid.NewGuid()), personId, VisitorContactDetailKind.Phone, phone, Now),
            CancellationToken.None);

        return personId;
    }

    private async Task SeedPersonNoteAsync(VisitorId personId, OperatorId authorId, string body)
    {
        await using var db = fixture.CreateDbContext();
        await new PersonNoteRepository(db).SaveAsync(
            PersonNote.Write(new PersonNoteId(Guid.NewGuid()), personId, authorId, body, Now), CancellationToken.None);
    }

    private async Task SeedContactDetailAsync(VisitorId personId, OperatorId recordedByOperatorId, string value)
    {
        await using var db = fixture.CreateDbContext();
        await new VisitorContactDetailRepository(db).SaveAsync(
            VisitorContactDetail.Record(
                new VisitorContactDetailId(Guid.NewGuid()), personId, VisitorContactDetailKind.Phone, value,
                recordedByOperatorId, Now),
            CancellationToken.None);
    }

    private async Task<SiteId> SeedSiteAsync(string name)
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", ["https://shop.example"], name));
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task<OperatorId> SeedOperatorAsync(SiteId siteId)
    {
        var subjectId = await fixture.CreateOperatorUserAsync($"person-erasure-op-{Guid.NewGuid():N}"[..24]);
        var operatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: subjectId));
        await db.SaveChangesAsync();

        return operatorId;
    }

    private async Task<int> CountAsync(string sql, Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql, new { id });
    }

    /// <summary>Raw `NpgsqlCommand`/`GetFieldValue&lt;DateTimeOffset&gt;`, not Dapper - a `timestamptz`
    /// comes back from Npgsql as a `DateTime`, and Dapper's own default handler cannot convert that to
    /// `DateTimeOffset` (`InvalidCastException`), the same reason <c>SiteErasureQuery.ListPendingAsync</c>
    /// reads its own <c>requested_at</c> column the identical way rather than through Dapper.</summary>
    private async Task<DateTimeOffset> GetErasureRequestedAtAsync(VisitorId personId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("select erasure_requested_at from visitors where id = @id", connection);
        command.Parameters.AddWithValue("id", personId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return reader.GetFieldValue<DateTimeOffset>(0);
    }
}
