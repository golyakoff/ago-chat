using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.MintDemoTenant;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Keycloak;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `8-07`'s Done-when, end to end against a real Postgres and a real Keycloak: a tenant is minted, its
/// credentials genuinely log in, and when its window passes <b>everything under it is gone</b> - proven
/// by letting one expire and then looking, not by reading the job.
///
/// <para><b>What is real here and what is not.</b> Postgres is real and every assertion about rows runs
/// against it. Keycloak is real, and so is <see cref="KeycloakDemoIdentityProvisioner"/> - the actual
/// adapter, with the actual service-account client, not a double. The object store is a recording
/// double (<see cref="RecordingFileStorage"/>): this item does not stand up a MinIO alongside the other
/// two containers, so what is proven is that the sweeper asks for every object key belonging to the
/// site before deleting the rows that name them. `5-02` separately proves a real S3 delete. That gap is
/// stated in `adr/0058` rather than papered over.</para>
/// </summary>
[Collection(DemoTenantCollection.Name)]
public class DemoTenantLifecycleTests(DemoTenantFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A settable clock, so a 24-hour window can pass in a millisecond. Declared here rather
    /// than borrowed from `Ago.Chat.Application.Tests` - test projects do not reference each other,
    /// and a shared-fakes project for one small class would be more machinery than the duplication
    /// costs.</summary>
    private sealed class SettableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private KeycloakDemoIdentityProvisioner CreateProvisioner(IClock clock) =>
        new(
            new HttpClient(),
            new KeycloakAdminOptions
            {
                BaseUrl = fixture.KeycloakBaseUrl,
                Realm = DemoTenantFixture.RealmName,
                ClientId = DemoTenantFixture.ProvisionerClientId,
                ClientSecret = DemoTenantFixture.ProvisionerClientSecret,
            },
            clock,
            NullLogger<KeycloakDemoIdentityProvisioner>.Instance);

    private MintDemoTenantHandler CreateHandler(IClock clock, IDemoIdentityProvisioner identities)
    {
        var db = fixture.CreateDbContext();
        return new(
            new DemoTenantRepository(fixture.DataSource),
            new SiteRegistrationRepository(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), clock),
            identities,
            new DemoCredentialGenerator(),
            new FakeRateLimiter(),
            new DemoTenantOptions
            {
                Enabled = true,
                VisitorOrigin = "https://demo.example",
                Lifetime = TimeSpan.FromHours(24),
                MaxLiveTenants = 100,
            },
            new DemoTenantRateLimitOptions(),
            new UuidV7Generator(),
            clock);
    }

    /// <summary>
    /// Done-when #1, as far as a test can carry it: the credentials a stranger is handed genuinely
    /// authenticate against the realm the console logs in to. Nobody intervened - one call produced
    /// them.
    /// </summary>
    [Fact]
    public async Task AMintedTenantsCredentialsActuallyLogIn()
    {
        var clock = new SettableClock(Now);
        var handler = CreateHandler(clock, CreateProvisioner(clock));

        var result = await handler.HandleAsync(new MintDemoTenant("203.0.113.1"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.ToString() : null);
        var login = await fixture.CanLogInAsync(result.Value.Username, result.Value.Password);
        Assert.True(login.Succeeded, $"The minted credentials did not authenticate: {login.Body}");
    }

    /// <summary>
    /// Done-when #2's mechanism: two mints are two operators, on two sites, with two identities. The
    /// item asks for this to be demonstrated with two browsers; what a test can establish is the
    /// property the browsers would be showing - that nothing is shared between them.
    /// </summary>
    [Fact]
    public async Task TwoMintsAreTwoTenantsWithNothingShared()
    {
        var clock = new SettableClock(Now);
        var handler = CreateHandler(clock, CreateProvisioner(clock));

        var first = await handler.HandleAsync(new MintDemoTenant("203.0.113.2"), CancellationToken.None);
        var second = await handler.HandleAsync(new MintDemoTenant("203.0.113.3"), CancellationToken.None);

        Assert.NotEqual(first.Value.Username, second.Value.Username);
        Assert.NotEqual(first.Value.SitePublicKey, second.Value.SitePublicKey);
        // Different sites is the part that matters: it is what makes one viewer's conversations
        // invisible to the other, which joining the shared demo site would not have done (`adr/0058`).
        Assert.NotEqual(await SiteIdOfAsync(first.Value.SitePublicKey), await SiteIdOfAsync(second.Value.SitePublicKey));
        Assert.True((await fixture.CanLogInAsync(first.Value.Username, first.Value.Password)).Succeeded);
        Assert.True((await fixture.CanLogInAsync(second.Value.Username, second.Value.Password)).Succeeded);
    }

    /// <summary>
    /// <b>Done-when #3, the one the item says must be proven by letting one expire.</b> A tenant is
    /// minted with real data underneath it - a visitor, a conversation, a message - the clock is moved
    /// past its window, one sweep runs, and then every table `personal-data.md` lists for a tenant is
    /// checked, plus the Keycloak user, plus whether the credentials still work.
    ///
    /// <para>Table by table rather than "the site row is gone": a deletion test that only checks what it
    /// remembers to check is exactly how erasure quietly becomes partial (`16-02`'s own Scope says so).
    /// The `DELETE` under test is one statement and relies on the schema's cascades - so the assertions
    /// have to be the independent half.</para>
    /// </summary>
    [Fact]
    public async Task WhenTheWindowPasses_TheTenantAndEverythingUnderItIsGone()
    {
        var clock = new SettableClock(Now);
        var provisioner = CreateProvisioner(clock);
        var handler = CreateHandler(clock, provisioner);

        var minted = await handler.HandleAsync(new MintDemoTenant("203.0.113.4"), CancellationToken.None);
        Assert.True(minted.IsSuccess);

        var siteId = await SiteIdOfAsync(minted.Value.SitePublicKey);
        var subjectId = await SubjectIdOfAsync(siteId);
        var conversationId = await SeedConversationAsync(siteId);
        await SeedAttachmentAsync(siteId, conversationId);

        Assert.True(await fixture.UserExistsAsync(subjectId));
        Assert.Equal(1, await CountAsync("select count(*) from messages m join conversations c on c.id = m.conversation_id where c.site_id = @siteId", siteId));

        // Past the window, by the clock the sweeper reads - nothing here waits 24 hours.
        clock.UtcNow = Now.AddHours(25);
        var storage = new RecordingFileStorage();
        var removed = await CreateSweepJob(clock, provisioner, storage).SweepAsync(CancellationToken.None);

        // At least one, not exactly one: this collection shares a database, so tenants other tests
        // minted are also past a window that jumped 25 hours. Everything below asserts against *this*
        // site's id, which is the claim that matters and the one that is independent of neighbours.
        Assert.True(removed >= 1, "The sweep removed nothing.");

        // Postgres: every table that can hold this tenant's data.
        Assert.Equal(0, await CountAsync("select count(*) from sites where id = @siteId", siteId));
        Assert.Equal(0, await CountAsync("select count(*) from visitors where site_id = @siteId", siteId));
        Assert.Equal(0, await CountAsync("select count(*) from conversations where site_id = @siteId", siteId));
        Assert.Equal(0, await CountAsync("select count(*) from operators where site_id = @siteId", siteId));
        Assert.Equal(0, await CountAsync("select count(*) from roles where site_id = @siteId", siteId));
        Assert.Equal(0, await CountAsync("select count(*) from attachments where site_id = @siteId", siteId));
        Assert.Equal(0, await CountAsync("select count(*) from channel_identities where site_id = @siteId", siteId));
        Assert.Equal(0, await CountAsync("select count(*) from webhook_endpoints where site_id = @siteId", siteId));
        // Messages hang off conversations, not off sites - the one table whose emptiness is two
        // cascades deep, and therefore the one most worth checking rather than assuming.
        Assert.Equal(0, await CountAsync(
            "select count(*) from messages m join conversations c on c.id = m.conversation_id where c.site_id = @siteId", siteId));

        // The identity provider, and the credentials themselves.
        Assert.False(await fixture.UserExistsAsync(subjectId));
        Assert.False(
            (await fixture.CanLogInAsync(minted.Value.Username, minted.Value.Password)).Succeeded,
            "The minted credentials still authenticate after the tenant expired.");

        // The object store: both of the attachment's keys were deleted - the object and `5-04`'s
        // thumbnail beside it. This is the step whose ordering matters most, because after the rows are
        // gone nothing can name the bytes any more, and `personal-data.md` already records that gap for
        // conversation deletion.
        Assert.Contains("demo/object-key", storage.Deleted);
        Assert.Contains("demo/thumb-key", storage.Deleted);
    }

    /// <summary>A tenant still inside its window is not touched - the other half of the sweep's
    /// predicate, and the one a bad `<=` would break silently while every expiry test still
    /// passed.</summary>
    [Fact]
    public async Task ATenantInsideItsWindowIsLeftAlone()
    {
        var clock = new SettableClock(Now);
        var provisioner = CreateProvisioner(clock);
        var minted = await CreateHandler(clock, provisioner)
            .HandleAsync(new MintDemoTenant("203.0.113.5"), CancellationToken.None);
        var siteId = await SiteIdOfAsync(minted.Value.SitePublicKey);

        clock.UtcNow = Now.AddHours(1);
        await CreateSweepJob(clock, provisioner, new RecordingFileStorage()).SweepAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync("select count(*) from sites where id = @siteId", siteId));
        Assert.True((await fixture.CanLogInAsync(minted.Value.Username, minted.Value.Password)).Succeeded);
    }

    /// <summary>
    /// The seeded `8-05` tenants stay. Represented here by any site with no `demo_expires_at` - which
    /// is what `create-demo-tenant.sh` produces, since nothing but this item's minting path ever sets
    /// that column. `8-07` is explicit that touching them means the change has gone wrong.
    /// </summary>
    [Fact]
    public async Task ASiteWithNoExpiryIsNeverSweptAwayHoweverOldItIs()
    {
        var seededId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(seededId, $"seed_{seededId.Value:N}", ["https://shop.example"], "Seeded demo shop"));
            await db.SaveChangesAsync();
        }

        var clock = new SettableClock(Now.AddYears(5));
        await CreateSweepJob(clock, CreateProvisioner(clock), new RecordingFileStorage())
            .SweepAsync(CancellationToken.None);

        Assert.Equal(1, await CountAsync("select count(*) from sites where id = @siteId", seededId));
    }

    /// <summary>
    /// The fact that decided <c>MintDemoTenantHandler</c>'s write ordering: <b>Keycloak assigns the
    /// subject id, and refuses one the caller chose.</b> Asserted directly against a real Keycloak
    /// because it is a claim about somebody else's software - and the first version of this item
    /// shipped the opposite assumption until this test contradicted it.
    /// </summary>
    [Fact]
    public async Task KeycloakAssignsTheSubjectId_AndDeletionByItIsIdempotent()
    {
        var clock = new SettableClock(Now);
        var provisioner = CreateProvisioner(clock);
        var created = await provisioner.CreateAsync(
            $"assigned-{Guid.NewGuid():N}"[..18], "assigned-id-test-password", CancellationToken.None);

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error!.Value.ToString() : null);
        // The id came from Keycloak's own Location header, and it is a real user.
        Assert.True(Guid.TryParse(created.Value, out _), $"Expected a UUID subject id, got '{created.Value}'.");
        Assert.True(await fixture.UserExistsAsync(created.Value));

        // And deletion by that id is idempotent, per the port's contract - the property that stops a
        // hand-deleted user wedging the sweeper forever.
        await provisioner.DeleteAsync(created.Value, CancellationToken.None);
        await provisioner.DeleteAsync(created.Value, CancellationToken.None);
        Assert.False(await fixture.UserExistsAsync(created.Value));
    }

    /// <summary>
    /// <b>The claim `adr/0058` rests on, tested directly instead of inferred - and it is not the claim
    /// this item first made.</b>
    ///
    /// <para>The handler was originally written to choose the subject id itself, write the operator row
    /// first, and let a half-failure self-heal. A `409 Conflict` was read as "Keycloak refuses a chosen
    /// id" and the ordering was rebuilt around that. The 409 was really a <em>username</em> collision -
    /// every mint inside the same minute produced the same username, cut from a UUIDv7 whose leading bits
    /// are the timestamp. This test separates the two, and the answer is worse than a refusal:
    /// <b>Keycloak answers 201 and silently assigns an id of its own</b>, ignoring the one supplied. Had
    /// the original design shipped, every minted operator row would have carried an id Keycloak never
    /// used, and every one of them would have failed to resolve - silently, because nothing would have
    /// errored. Reading the id back from the `Location` header is not a workaround for a refusal; it is
    /// the only way to know what the identity actually is.</para>
    /// </summary>
    [Fact]
    public async Task KeycloakSilentlyIgnoresACallerChosenUserId()
    {
        var chosen = Guid.NewGuid().ToString();

        var (status, assignedId) = await fixture.CreateUserWithChosenIdAsync(
            chosen, $"probe-{Guid.NewGuid():N}"[..14]);

        Assert.Equal(201, status);
        Assert.NotNull(assignedId);
        // The whole point: accepted, and not the id that was asked for.
        Assert.NotEqual(chosen, assignedId);
        Assert.False(await fixture.UserExistsAsync(chosen), "Keycloak honoured the chosen id after all.");
        Assert.True(await fixture.UserExistsAsync(assignedId!));
    }

    private DemoTenantExpiryJob CreateSweepJob(IClock clock, IDemoIdentityProvisioner identities, IFileStorage storage) =>
        new(
            new DemoTenantRepository(fixture.DataSource),
            identities,
            storage,
            clock,
            Options.Create(new DemoTenantExpiryJobOptions()),
            NullLogger<DemoTenantExpiryJob>.Instance);

    private async Task<SiteId> SiteIdOfAsync(string publicKey)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return new SiteId(await connection.ExecuteScalarAsync<Guid>(
            "select id from sites where public_key = @publicKey", new { publicKey }));
    }

    private async Task<string> SubjectIdOfAsync(SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string>(
            "select external_subject_id from operators where site_id = @siteId",
            new { siteId = siteId.Value }) ?? throw new InvalidOperationException("No operator for site.");
    }

    private async Task<int> CountAsync(string sql, SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql, new { siteId = siteId.Value });
    }

    /// <summary>A visitor, a conversation and a message on the minted tenant, so the expiry assertions
    /// are about a tenant that actually held something. A deletion proven only against empty tables
    /// proves that empty tables stay empty.</summary>
    private async Task<ConversationId> SeedConversationAsync(SiteId siteId)
    {
        var now = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello from the demo"), now);

        await using var db = fixture.CreateDbContext();
        db.Visitors.Add(new Visitor(visitorId, siteId, now));
        await db.SaveChangesAsync();
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    /// <summary>An attachment row with an object key and a thumbnail key, inserted directly - the
    /// aggregate's own state machine (pending -> ready) is `5-03`'s business, and what this test needs
    /// is simply a row naming two objects so the sweep has something to delete.</summary>
    private async Task SeedAttachmentAsync(SiteId siteId, ConversationId conversationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into attachments
                (id, site_id, conversation_id, object_key, content_type, size_bytes, state, created_at, thumbnail_key)
            values (@id, @siteId, @conversationId, 'demo/object-key', 'image/png', 123, 'Ready', now(), 'demo/thumb-key')
            """,
            new
            {
                id = Guid.NewGuid(),
                siteId = siteId.Value,
                conversationId = conversationId.Value,
            });
    }

    /// <summary>Records which sites it was asked about and which keys it was told to delete. The object
    /// store is the one dependency this fixture does not run for real - see the class remarks.</summary>
    /// <summary>Records what it was told to delete. The object store is the one dependency this
    /// fixture does not run for real - see the class remarks for why, and `adr/0058` for the gap that
    /// leaves.</summary>
    private sealed class RecordingFileStorage : IFileStorage
    {
        private readonly List<string> _deleted = [];

        public IReadOnlyList<string> Deleted => _deleted;

        public Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
        {
            _deleted.Add(key.Value);
            return Task.CompletedTask;
        }

        public Task<PresignedUpload> CreateUploadAsync(
            ObjectKey key, UploadConstraints constraints, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreateDownloadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ObjectMetadata?> GetMetadataAsync(ObjectKey key, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectMetadata?>(null);
    }
}
