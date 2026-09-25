using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `adr/0184`: chat is the account's person registry - against a real Postgres. The registration store's
/// idempotency is the primary key's, not a pre-read's, so it has to be proven here and not with a fake;
/// the person-note repository is the second table this decision adds; and the schema guard that used to
/// live in <c>ContactCollectedOutboxTests</c> stays - chat still holds no <c>customers</c> table, and now
/// neither does anybody else.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PersonRegistryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ChatsOwnSchema_HoldsNoCustomersTable()
    {
        await using var db = fixture.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('public.customers') IS NOT NULL";
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        Assert.False((bool)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RegisteringANewPerson_CreatesTheVisitorAndTheirDetails_Together()
    {
        var siteId = await ASiteAsync();
        var personId = new VisitorId(Guid.NewGuid());
        var person = new Visitor(personId, siteId, Now);
        person.AssignEmojiPair("🦊", "🍐");
        var phone = VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), personId, VisitorContactDetailKind.Phone, "+79990000001", Now);

        PersonRegistrationOutcome outcome;
        await using (var db = fixture.CreateDbContext())
        {
            outcome = await new PersonRegistrationStore(db).RegisterIfAbsentAsync(person, [phone], CancellationToken.None);
        }

        Assert.Equal(PersonRegistrationOutcome.Created, outcome);

        await using var verify = fixture.CreateDbContext();
        var stored = await verify.Visitors.SingleAsync(v => v.Id == personId);
        Assert.Equal(siteId, stored.SiteId);
        Assert.Equal("🦊", stored.EmojiCreature);
        var storedPhone = Assert.Single(await verify.VisitorContactDetails.Where(d => d.VisitorId == personId).ToListAsync());
        Assert.Equal("+79990000001", storedPhone.Value);
        Assert.Equal(VisitorContactDetailSource.Visitor, storedPhone.Source);
    }

    /// <summary>CLAUDE.md rule 5: a redelivery finds the person present and changes nothing - not the
    /// visitor row, and not the details, even when the redelivered announcement carries a different
    /// phone. The registry's own facts outrank a booking form's.</summary>
    [Fact]
    public async Task RegisteringAnExistingPerson_ChangesNothing_AndReportsAlreadyExisted()
    {
        var siteId = await ASiteAsync();
        var personId = new VisitorId(Guid.NewGuid());
        var first = new Visitor(personId, siteId, Now);
        var firstPhone = VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), personId, VisitorContactDetailKind.Phone, "+79990000001", Now);
        await using (var db = fixture.CreateDbContext())
        {
            await new PersonRegistrationStore(db).RegisterIfAbsentAsync(first, [firstPhone], CancellationToken.None);
        }

        var again = new Visitor(personId, siteId, Now.AddDays(1));
        var otherPhone = VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), personId, VisitorContactDetailKind.Phone, "+79990000002", Now.AddDays(1));
        PersonRegistrationOutcome outcome;
        await using (var db = fixture.CreateDbContext())
        {
            outcome = await new PersonRegistrationStore(db).RegisterIfAbsentAsync(again, [otherPhone], CancellationToken.None);
        }

        Assert.Equal(PersonRegistrationOutcome.AlreadyExisted, outcome);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(Now, (await verify.Visitors.SingleAsync(v => v.Id == personId)).FirstSeenAt);
        var detail = Assert.Single(await verify.VisitorContactDetails.Where(d => d.VisitorId == personId).ToListAsync());
        Assert.Equal("+79990000001", detail.Value);
    }

    [Fact]
    public async Task RegisteringAPersonForAnUnknownAccount_WritesNothing_AndSaysSo()
    {
        var personId = new VisitorId(Guid.NewGuid());
        var person = new Visitor(personId, new SiteId(Guid.NewGuid()), Now);

        PersonRegistrationOutcome outcome;
        await using (var db = fixture.CreateDbContext())
        {
            outcome = await new PersonRegistrationStore(db).RegisterIfAbsentAsync(person, [], CancellationToken.None);
        }

        Assert.Equal(PersonRegistrationOutcome.SiteUnknown, outcome);

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Visitors.AnyAsync(v => v.Id == personId));
    }

    [Fact]
    public async Task PersonNotes_RoundTrip_OldestFirst_AndGoWithThePerson()
    {
        var siteId = await ASiteAsync();
        var personId = new VisitorId(Guid.NewGuid());
        var author = new OperatorId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Visitors.Add(new Visitor(personId, siteId, Now));
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var notes = new PersonNoteRepository(db);
            await notes.SaveAsync(PersonNote.Write(new PersonNoteId(Guid.NewGuid()), personId, author, "second", Now.AddMinutes(1)), CancellationToken.None);
            await notes.SaveAsync(PersonNote.Write(new PersonNoteId(Guid.NewGuid()), personId, author, "first", Now), CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var read = await new PersonNoteRepository(db).GetForPersonAsync(personId, CancellationToken.None);
            Assert.Equal(["first", "second"], read.Select(n => n.Body));
        }

        // The cascade that backs the explicit erasure drain: no person, no notes about them.
        await using (var db = fixture.CreateDbContext())
        {
            await db.Visitors.Where(v => v.Id == personId).ExecuteDeleteAsync();
            Assert.Equal(0, await db.PersonNotes.CountAsync(n => n.PersonId == personId));
        }
    }

    private async Task<SiteId> ASiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", ["https://example.test"], "Test Site", Now));
        await seed.SaveChangesAsync();
        return siteId;
    }
}
