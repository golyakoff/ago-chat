using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `12-02`: a Postgres container this collection <b>owns outright</b>, seeded once with a fully
/// known set of tenants.
///
/// <para><b>Why not the shared <see cref="PostgresFixture"/>.</b> Every other read model in this
/// suite is tenant-scoped, so a test can isolate itself with fresh ids and simply ignore whatever
/// other classes left in the database (that fixture's own remarks). `12-02`'s query is the one that
/// cannot do that: it returns <i>every</i> site there is, so "assert the returned numbers match
/// ground truth" is only a meaningful claim in a database whose entire contents the test controls -
/// and its keyset pagination can only be checked for gaps and duplicates against a known, complete
/// list. Hence a dedicated container, the same "one fixture per genuinely-needed resource situation"
/// precedent <see cref="AttachmentFixture"/>/<see cref="SiteCachingFixture"/> already set.</para>
///
/// <para>Seeding happens once here rather than per test, and every test against it is read-only, so
/// the two test classes in this collection cannot perturb each other's expectations
/// (<see cref="OperatorOidcFixture"/> uses the same arrangement for the same reason).</para>
/// </summary>
public sealed class PlatformOverviewFixture : IAsyncLifetime
{
    /// <summary>The window width the seeded data is designed around - deliberately the same 30 days
    /// <c>ListSitesForOwnerHandler.RecentWindowDays</c> uses, so the fixture's own "recent" and
    /// "older" message timestamps mean the same thing the production handler means by them. Tests
    /// pass <see cref="RecentSince"/> to the read store explicitly rather than going through a clock,
    /// because the port takes an instant, not a policy.</summary>
    public const int WindowDays = 30;

    private const string ContentType = "image/png";

    private PostgreSqlContainer _container = null!;
    private IDisposable _dockerLock = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>The single instant every seeded timestamp is relative to, captured once so that a
    /// test's expectation and the seeded row can never disagree about "now".</summary>
    public DateTimeOffset Now { get; private set; }

    public DateTimeOffset RecentSince => Now.AddDays(-WindowDays);

    /// <summary>
    /// The complete, deliberately varied ground truth: different seat counts, different conversation
    /// counts, message volumes that straddle the window boundary <b>and</b> more than one monthly
    /// `messages` partition, and different attachment byte totals - including the three states that
    /// have to be told apart. Ids are fixed and ordered so that the keyset page order
    /// (`id desc`) is known in advance: Epsilon, Delta, Gamma, Beta, Alpha.
    /// </summary>
    public static IReadOnlyList<SeededSite> Plan { get; } =
    [
        // Two conversations, messages inside the window in two different months (-1/-2/-3 days and
        // -28 days), plus one well outside it in an older partition. Two live attachments and one
        // deleted one that must not be counted.
        new SeededSite(
            Id: SiteIdFrom(1),
            Name: "Alpha Shop",
            CreatedAtDaysAgo: 100,
            Operators: 3,
            Conversations: 2,
            MessageDaysAgo: [1, 2, 3, 28, 95],
            ReadyAttachmentBytes: [1_000, 2_500],
            PendingAttachmentBytes: [],
            DeletedAttachmentBytes: [999_999]),

        // One seat, one conversation, one message in and one out of the window, no attachments at
        // all - the `SUM` over no rows that must come back as 0, not null.
        new SeededSite(
            Id: SiteIdFrom(2),
            Name: "Beta Bakery",
            CreatedAtDaysAgo: 50,
            Operators: 1,
            Conversations: 1,
            MessageDaysAgo: [10, 40],
            ReadyAttachmentBytes: [],
            PendingAttachmentBytes: [],
            DeletedAttachmentBytes: []),

        // Registered and then never used: no operators, no conversations, no messages, nothing
        // stored. Also the row with no recorded creation time (`sites.created_at` null), standing in
        // for every site that predates `Stage12AddSiteCreatedAt`.
        new SeededSite(
            Id: SiteIdFrom(3),
            Name: "Gamma Garage",
            CreatedAtDaysAgo: null,
            Operators: 0,
            Conversations: 0,
            MessageDaysAgo: [],
            ReadyAttachmentBytes: [],
            PendingAttachmentBytes: [],
            DeletedAttachmentBytes: []),

        // The case the window exists to make visible: a tenant with real history, all of it older
        // than the window. Recent volume 0 and last activity null, while its conversation count
        // stays non-zero - so "no activity in the window" cannot be confused with "never existed".
        // Its one pending attachment is counted (non-deleted) and its deleted one is not.
        new SeededSite(
            Id: SiteIdFrom(4),
            Name: "Delta Diner",
            CreatedAtDaysAgo: 10,
            Operators: 2,
            Conversations: 1,
            MessageDaysAgo: [45, 60],
            ReadyAttachmentBytes: [],
            PendingAttachmentBytes: [777],
            DeletedAttachmentBytes: [5]),

        new SeededSite(
            Id: SiteIdFrom(5),
            Name: "Epsilon Electric",
            CreatedAtDaysAgo: 1,
            Operators: 1,
            Conversations: 3,
            MessageDaysAgo: [5],
            ReadyAttachmentBytes: [4_096],
            PendingAttachmentBytes: [],
            DeletedAttachmentBytes: []),
    ];

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();

        DataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();

        await using (var db = CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        Now = DateTimeOffset.UtcNow;

        await EnsureMessagePartitionsAsync();
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
        _dockerLock.Dispose();
    }

    public AgoChatDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(DataSource).Options;
        return new AgoChatDbContext(options);
    }

    /// <summary>`Stage2PartitionMessages` creates the current month plus two ahead, and
    /// `PartitionMaintenanceJob` (a Worker background service, not running here) keeps that true
    /// going forward - neither creates partitions in the <i>past</i>, so seeding a message dated 95
    /// days ago would simply be rejected. Creating one partition per month the plan actually touches
    /// is what makes "spanning both a recent and an older partition" real rather than nominal: the
    /// bounded-window query has genuinely-older partitions available to skip.</summary>
    private async Task EnsureMessagePartitionsAsync()
    {
        var months = Plan
            .SelectMany(site => site.MessageDaysAgo)
            .Select(daysAgo => Now.AddDays(-daysAgo))
            .Select(at => new DateTimeOffset(at.Year, at.Month, 1, 0, 0, 0, TimeSpan.Zero))
            .Distinct();

        await using var connection = await DataSource.OpenConnectionAsync();
        foreach (var from in months)
        {
            var to = from.AddMonths(1);
            // `13-06`: messages is now PARTITION BY LIST (retention_class) then RANGE (created_at) -
            // every message this fixture seeds is written through Conversation.AddVisitorMessage with
            // no retentionClass argument, which defaults to RetentionClass.Free (that method's own
            // remarks), so every leaf this fixture needs hangs off the `free` class partition.
            var partitionName = MessagePartitionNames.ForMonth(RetentionClass.Free, from);
            var sql = $"""
                CREATE TABLE IF NOT EXISTS {partitionName} PARTITION OF {MessagePartitionNames.ForClass(RetentionClass.Free)}
                    FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}');
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task SeedAsync()
    {
        foreach (var plan in Plan)
        {
            var visitorId = new VisitorId(Guid.NewGuid());
            var conversationIds = Enumerable.Range(0, plan.Conversations)
                .Select(_ => new ConversationId(Guid.NewGuid()))
                .ToList();

            await using (var db = CreateDbContext())
            {
                db.Sites.Add(new Site(
                    plan.Id,
                    $"site_{plan.Id.Value:N}",
                    [],
                    plan.Name,
                    plan.CreatedAtDaysAgo is { } daysAgo ? Now.AddDays(-daysAgo) : null));

                for (var i = 0; i < plan.Operators; i++)
                {
                    db.Operators.Add(new Operator(
                        new OperatorId(Guid.NewGuid()), plan.Id, OperatorStatus.Offline, capacity: 5));
                }

                if (conversationIds.Count > 0)
                {
                    db.Visitors.Add(new Visitor(visitorId, plan.Id, Now));
                }

                await db.SaveChangesAsync();
            }

            await using (var db = CreateDbContext())
            {
                foreach (var conversationId in conversationIds)
                {
                    db.Conversations.Add(Conversation.Start(conversationId, plan.Id, visitorId, Now));
                }

                await db.SaveChangesAsync();
            }

            if (conversationIds.Count > 0)
            {
                await SeedMessagesAsync(plan, conversationIds[0], visitorId);
                await SeedAttachmentsAsync(plan, conversationIds[0]);
            }
        }
    }

    /// <summary>Raw SQL, not the <c>Conversation</c> aggregate: these rows need arbitrary
    /// <c>created_at</c> values (that is the entire point - some inside the window, some in an older
    /// partition), and the aggregate deliberately stamps its own from the clock it is handed.
    /// <see cref="MessagePartitioningTests"/> already inserts this way for the same reason.</summary>
    private async Task SeedMessagesAsync(SeededSite plan, ConversationId conversationId, VisitorId visitorId)
    {
        var sequence = 0;
        await using var connection = await DataSource.OpenConnectionAsync();
        foreach (var daysAgo in plan.MessageDaysAgo)
        {
            sequence++;
            await using var command = new NpgsqlCommand("""
                insert into messages (id, conversation_id, sequence, author_kind, author_id, body, created_at, retention_class)
                values (@id, @conversationId, @sequence, 'Visitor', @authorId, 'seeded', @createdAt, 'free')
                """, connection);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("conversationId", conversationId.Value);
            command.Parameters.AddWithValue("sequence", sequence);
            command.Parameters.AddWithValue("authorId", visitorId.Value);
            command.Parameters.AddWithValue("createdAt", Now.AddDays(-daysAgo));
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Through the real <see cref="Attachment"/> aggregate, unlike the messages above:
    /// the read store's SQL filters on the <c>state</c> string EF writes, so letting the domain's own
    /// <see cref="Attachment.ConfirmReady"/>/<see cref="Attachment.MarkDeleted"/> produce those values
    /// is what makes the filter's agreement with them a tested fact rather than a matching pair of
    /// string literals.</summary>
    private async Task SeedAttachmentsAsync(SeededSite plan, ConversationId conversationId)
    {
        await using var db = CreateDbContext();

        foreach (var size in plan.ReadyAttachmentBytes)
        {
            var attachment = NewAttachment(plan, conversationId, size);
            attachment.ConfirmReady(size, ContentType, Now);
            db.Attachments.Add(attachment);
        }

        foreach (var size in plan.PendingAttachmentBytes)
        {
            db.Attachments.Add(NewAttachment(plan, conversationId, size));
        }

        foreach (var size in plan.DeletedAttachmentBytes)
        {
            var attachment = NewAttachment(plan, conversationId, size);
            attachment.ConfirmReady(size, ContentType, Now);
            attachment.MarkDeleted();
            db.Attachments.Add(attachment);
        }

        await db.SaveChangesAsync();
    }

    private Attachment NewAttachment(SeededSite plan, ConversationId conversationId, long size)
    {
        var id = new AttachmentId(Guid.NewGuid());
        return Attachment.CreatePending(
            id, plan.Id, conversationId, $"seed/{id.Value:N}", ContentType, size, Now);
    }

    /// <summary>Fixed, ordered ids so `order by id desc` is predictable in the test rather than
    /// discovered from the result it is supposed to be checking.</summary>
    private static SiteId SiteIdFrom(int ordinal) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{ordinal:000000000000}"));
}

/// <summary>`12-02`: one tenant's seeded shape - the ground truth
/// <see cref="PlatformOverviewReadStoreTests"/> computes its expectations from, so an expectation and
/// the row it describes are written down exactly once.</summary>
/// <param name="MessageDaysAgo">How many days before <see cref="PlatformOverviewFixture.Now"/> each
/// message was sent. Values above <see cref="PlatformOverviewFixture.WindowDays"/> are outside the
/// recent window on purpose.</param>
/// <param name="PendingAttachmentBytes">Attachments presigned but never confirmed. Counted in the
/// byte total, because `12-02` asks for the site's <i>non-deleted</i> attachments - a declared size
/// for an upload that may never have completed, which `5-04`'s orphan sweep is what eventually
/// removes. Seeded explicitly so that inclusion is a decision this suite proves, not an accident.
/// </param>
public sealed record SeededSite(
    SiteId Id,
    string Name,
    int? CreatedAtDaysAgo,
    int Operators,
    int Conversations,
    IReadOnlyList<int> MessageDaysAgo,
    IReadOnlyList<long> ReadyAttachmentBytes,
    IReadOnlyList<long> PendingAttachmentBytes,
    IReadOnlyList<long> DeletedAttachmentBytes);

[CollectionDefinition(Name)]
public sealed class PlatformOverviewCollection : ICollectionFixture<PlatformOverviewFixture>
{
    public const string Name = "PlatformOverview";
}
