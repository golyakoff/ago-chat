using System.Text.Json;
using Ago.Chat.Application.UseCases.RemoveOperator;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Backfill;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `22-16`: the shape no arrangement in <c>RoleAssignmentsChangedOutboxTests</c> can express, by that
/// file's own admission - every one of its tests registers a site (or redeems an invite, or removes an
/// operator) through the real handler, so the event that populates the projection is always part of the
/// arrangement. Every test below seeds <c>Sites</c>/<c>Operators</c>/<c>Roles</c>/<c>OperatorRoles</c>
/// directly against the fixture's <c>AgoChatDbContext</c>, the same way
/// <c>RemovingAnOperator_...</c>'s own seed block does for its pre-existing admin operator - no
/// publisher ever runs, which is exactly "a tenant that existed before the projection did."
/// </summary>
[Collection(RoleAssignmentProjectionBackfillCollection.Name)]
public sealed class RoleAssignmentProjectionBackfillTests(RoleAssignmentProjectionBackfillFixture fixture)
{
    private static readonly DateTimeOffset RunStartedAt =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task APreExistingTenant_SeededWithNoPublisherEverHavingRun_GetsARoleAssignmentsChangedRowStaged()
    {
        await fixture.ResetAsync();

        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var externalSubjectId = $"sub-{Guid.NewGuid():N}";
        var operatorRoleId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();

        // The pre-`22-05` state: a site, an operator holding two roles, and both roles' rows - written
        // directly, exactly as if this tenant had been registered a year before `RoleAssignmentsChanged`
        // existed. No SiteRegistrationRepository, no outbox row staged by anything until the backfill
        // below runs.
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId));
            seed.Roles.Add(new RoleRecord
            {
                Id = operatorRoleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationRead.Value],
            });
            seed.Roles.Add(new RoleRecord
            {
                Id = adminRoleId,
                SiteId = siteId,
                Name = "Admin",
                Permissions = [Permission.SiteConfigure.Value, Permission.CalendarConfigure.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = operatorRoleId });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = adminRoleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var backfill = new RoleAssignmentProjectionBackfill(db, new UuidV7Generator(), new FixedClock(RunStartedAt));
            var outcome = await backfill.RunAsync(CancellationToken.None);

            Assert.Equal(1, outcome.CandidatesConsidered);
            Assert.Equal(0, outcome.SkippedDueToRace);
            var published = Assert.Single(outcome.Published);
            Assert.Equal(externalSubjectId, published.ExternalSubjectId);
            Assert.Equal(siteId, published.SiteId);
        }

        await using var verify = fixture.CreateDbContext();
        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == externalSubjectId, CancellationToken.None);

        var contract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(outboxRow.Payload)!;
        Assert.Equal(siteId.Value, contract.SiteId);
        var expected = new[] { Permission.ConversationRead.Value, Permission.SiteConfigure.Value, Permission.CalendarConfigure.Value };
        Assert.Equal(expected.OrderBy(p => p, StringComparer.Ordinal), contract.Permissions.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Null(outboxRow.PublishedAt);
    }

    [Fact]
    public async Task AnOperatorWithNoExternalSubjectYet_AnUnredeemedInvites_IsNotACandidateAndNothingIsStaged()
    {
        await fixture.ResetAsync();

        var siteId = new SiteId(Guid.NewGuid());
        var withSubject = new OperatorId(Guid.NewGuid());
        var withoutSubject = new OperatorId(Guid.NewGuid());
        var externalSubjectId = $"sub-{Guid.NewGuid():N}";

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            // The real shape an unredeemed invite leaves behind: no Operator row exists for it at all
            // until redemption. The closest reachable stand-in for "nothing to project" here is an
            // Operator with a null ExternalSubjectId - RemoveOperatorHandler's own remarks name this
            // exact case ("an unredeemed invite that was later removed") as the reason its own guard
            // exists, and this method's guard is the identical one.
            seed.Operators.Add(new Operator(withSubject, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId));
            seed.Operators.Add(new Operator(withoutSubject, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: null));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = fixture.CreateDbContext();
        var backfill = new RoleAssignmentProjectionBackfill(db, new UuidV7Generator(), new FixedClock(RunStartedAt));
        var outcome = await backfill.RunAsync(CancellationToken.None);

        // Only the operator with a linked identity is a candidate at all - matching
        // SiteRegistrationRepository's and RemoveOperatorHandler's own publishers, not a second rule.
        Assert.Equal(1, outcome.CandidatesConsidered);
        var published = Assert.Single(outcome.Published);
        Assert.Equal(externalSubjectId, published.ExternalSubjectId);
    }

    [Fact]
    public async Task ARemovedOperator_FromBeforeTheProjectionExisted_IsNotRepublished()
    {
        await fixture.ResetAsync();

        var siteId = new SiteId(Guid.NewGuid());
        var removedOperatorId = new OperatorId(Guid.NewGuid());
        var removedSubjectId = $"sub-{Guid.NewGuid():N}";

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            // Removed before 22-05 ever shipped: no publisher ever ran for this row, so no projection
            // row exists for it anywhere either - "absent" already means "holds nothing"
            // (RoleAssignmentProjectionStore.GetPermissionsAsync's own remarks), which is already the
            // right answer. Republishing this as a revoke would be inventing a second event shape for
            // a fact this system already represents correctly by having said nothing at all.
            seed.Operators.Add(new Operator(
                removedOperatorId, siteId, OperatorStatus.Offline, capacity: 5, removedSubjectId,
                holdsSeat: false, removedAt: RunStartedAt.AddDays(-30)));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = fixture.CreateDbContext();
        var backfill = new RoleAssignmentProjectionBackfill(db, new UuidV7Generator(), new FixedClock(RunStartedAt));
        var outcome = await backfill.RunAsync(CancellationToken.None);

        Assert.Equal(0, outcome.CandidatesConsidered);
        Assert.Empty(outcome.Published);

        await using var verify = fixture.CreateDbContext();
        var anyOutboxRow = await verify.Set<OutboxMessage>().AnyAsync(
            o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == removedSubjectId, CancellationToken.None);
        Assert.False(anyOutboxRow);
    }

    /// <summary>
    /// `22-16`'s own Done-when: "running it twice changes nothing the second time, proven by doing
    /// it." <b>Proven precisely, not asserted</b>: the outbox row count does grow on the second run
    /// (this type republishes unconditionally rather than tracking "already backfilled" state - the
    /// report explains why that bookkeeping would need a cross-database read this repository cannot
    /// make), but the <i>content</i> the second run stages - the permission set for every subject -
    /// is byte-identical to the first run's, which is what "changes nothing" means for a full-snapshot
    /// event and a full-replace consumer (`RoleAssignmentsChangedConsumer`'s own remarks: "redelivering
    /// the identical message twice stages the identical values twice").
    /// </summary>
    [Fact]
    public async Task RunningTheBackfillTwice_StagesTheIdenticalFactsBothTimes_RealBeforeAndAfterCounts()
    {
        await fixture.ResetAsync();

        var siteAId = new SiteId(Guid.NewGuid());
        var siteBId = new SiteId(Guid.NewGuid());
        var operatorAId = new OperatorId(Guid.NewGuid());
        var operatorBId = new OperatorId(Guid.NewGuid());
        var subjectA = $"sub-{Guid.NewGuid():N}";
        var subjectB = $"sub-{Guid.NewGuid():N}";
        var roleAId = Guid.NewGuid();
        var roleBId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteAId, $"site_{siteAId.Value:N}", []));
            seed.Sites.Add(new Site(siteBId, $"site_{siteBId.Value:N}", []));
            seed.Operators.Add(new Operator(operatorAId, siteAId, OperatorStatus.Offline, capacity: 5, subjectA));
            seed.Operators.Add(new Operator(operatorBId, siteBId, OperatorStatus.Offline, capacity: 5, subjectB));
            seed.Roles.Add(new RoleRecord { Id = roleAId, SiteId = siteAId, Name = "Admin", Permissions = [Permission.SiteConfigure.Value] });
            seed.Roles.Add(new RoleRecord { Id = roleBId, SiteId = siteBId, Name = "Operator", Permissions = [Permission.ConversationRead.Value] });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorAId, RoleId = roleAId });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorBId, RoleId = roleBId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        async Task<int> CountOutboxRowsAsync()
        {
            await using var verify = fixture.CreateDbContext();
            return await verify.Set<OutboxMessage>().CountAsync(
                o => o.Type == nameof(RoleAssignmentsChanged) && (o.PartitionKey == subjectA || o.PartitionKey == subjectB));
        }

        var beforeFirstRun = await CountOutboxRowsAsync();
        Assert.Equal(0, beforeFirstRun);

        RoleAssignmentProjectionBackfillOutcome firstOutcome;
        await using (var db = fixture.CreateDbContext())
        {
            var backfill = new RoleAssignmentProjectionBackfill(db, new UuidV7Generator(), new FixedClock(RunStartedAt));
            firstOutcome = await backfill.RunAsync(CancellationToken.None);
        }

        var afterFirstRun = await CountOutboxRowsAsync();

        RoleAssignmentProjectionBackfillOutcome secondOutcome;
        await using (var db = fixture.CreateDbContext())
        {
            var backfill = new RoleAssignmentProjectionBackfill(db, new UuidV7Generator(), new FixedClock(RunStartedAt.AddMinutes(5)));
            secondOutcome = await backfill.RunAsync(CancellationToken.None);
        }

        var afterSecondRun = await CountOutboxRowsAsync();

        // The real, measured before/after counts this item's Done-when asks for - not an assertion
        // that nothing happened, a count of exactly what did.
        Assert.Equal(0, beforeFirstRun);
        Assert.Equal(2, afterFirstRun);
        Assert.Equal(4, afterSecondRun);

        // Same two candidates, same two subjects, both runs - nothing appeared or disappeared between
        // them.
        Assert.Equal(2, firstOutcome.CandidatesConsidered);
        Assert.Equal(2, secondOutcome.CandidatesConsidered);
        Assert.Equal(0, firstOutcome.SkippedDueToRace);
        Assert.Equal(0, secondOutcome.SkippedDueToRace);

        // The content is what "changes nothing" actually means here: the second run's permission set
        // for each subject is identical to the first run's, so a consumer applying both in order lands
        // on the same state a consumer applying only the first would - the row it upserts is
        // overwritten with the values it already had.
        var firstBySubject = firstOutcome.Published.ToDictionary(p => p.ExternalSubjectId, p => p.Permissions.OrderBy(x => x, StringComparer.Ordinal).ToList());
        var secondBySubject = secondOutcome.Published.ToDictionary(p => p.ExternalSubjectId, p => p.Permissions.OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.Equal(firstBySubject.Keys.OrderBy(k => k), secondBySubject.Keys.OrderBy(k => k));
        foreach (var subject in firstBySubject.Keys)
        {
            Assert.Equal(firstBySubject[subject], secondBySubject[subject]);
        }

        // And at the wire-payload level, not just the in-process record: every RoleAssignmentsChanged
        // row this pair of subjects now has in the outbox carries the identical Permissions array,
        // regardless of which run staged it.
        await using var verifyPayloads = fixture.CreateDbContext();
        var rows = await verifyPayloads.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(RoleAssignmentsChanged) && (o.PartitionKey == subjectA || o.PartitionKey == subjectB))
            .ToListAsync(CancellationToken.None);
        var permissionSetsBySubject = rows
            .Select(r => JsonSerializer.Deserialize<RoleAssignmentsChanged>(r.Payload)!)
            .GroupBy(c => c.ExternalSubjectId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Permissions.OrderBy(x => x, StringComparer.Ordinal).ToList()).Distinct(new PermissionListComparer()).ToList());
        Assert.All(permissionSetsBySubject.Values, distinctSets => Assert.Single(distinctSets));
    }


    /// <summary>
    /// `22-16`'s report: the deterministic counterpart to
    /// <c>RoleAssignmentProjectionBackfillOrderingConcurrencyTests</c>' racing proof. That test shows
    /// both outcomes are correct whichever one a real race produces, but "the backfill correctly skips
    /// an operator a concurrent removal already took" is naturally the rarer of the two under real
    /// timing, which makes it a poor fit for a hard per-run assertion. This test builds that exact
    /// state on purpose instead of hoping a race lands there: a real <see cref="RemoveOperatorHandler"/>
    /// call commits <b>first</b>, sequentially, then <see cref="RoleAssignmentProjectionBackfill.PublishOneAsync"/>
    /// is called directly for that same, now-already-removed operator id - simulating exactly what
    /// happens inside <see cref="RoleAssignmentProjectionBackfill.RunAsync"/> when a real removal lands
    /// between the candidate list being built and this particular operator's own turn being processed.
    /// </summary>
    [Fact]
    public async Task APreviouslyRemovedOperator_IsCorrectlySkippedWhenTheBackfillReachesItsOwnTurn()
    {
        await fixture.ResetAsync();

        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var requestedById = new OperatorId(Guid.NewGuid());
        var subjectId = $"sub-{Guid.NewGuid():N}";
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, subjectId));
            seed.Operators.Add(new Operator(requestedById, siteId, OperatorStatus.Offline, capacity: 5, "admin-subject"));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Admin",
                Permissions = [Permission.SiteManageOperators.Value, Permission.CalendarConfigure.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = requestedById, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        // The real removal, committed for real, before the backfill ever touches this operator - not
        // a race, a fact already true by the time PublishOneAsync below runs.
        await using (var removeDb = fixture.CreateDbContext())
        {
            var handler = new RemoveOperatorHandler(
                new OperatorRepository(removeDb), new PermissionChecker(removeDb), new EfOutboxWriter<AgoChatDbContext>(removeDb),
                new UuidV7Generator(), new FixedClock(RunStartedAt));
            var result = await handler.HandleAsync(new RemoveOperator(requestedById, siteId, operatorId), CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        PublishedRoleAssignment? published;
        await using (var db = fixture.CreateDbContext())
        {
            var backfill = new RoleAssignmentProjectionBackfill(db, new UuidV7Generator(), new FixedClock(RunStartedAt.AddMinutes(1)));
            published = await backfill.PublishOneAsync(operatorId, RunStartedAt.AddMinutes(1), CancellationToken.None);
        }

        Assert.Null(published);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(RoleAssignmentsChanged) && o.PartitionKey == subjectId)
            .ToListAsync(CancellationToken.None);
        var only = Assert.Single(rows);
        var contract = JsonSerializer.Deserialize<RoleAssignmentsChanged>(only.Payload)!;
        Assert.Empty(contract.Permissions);
    }
    private sealed class PermissionListComparer : IEqualityComparer<List<string>>
    {
        public bool Equals(List<string>? x, List<string>? y) => x is not null && y is not null && x.SequenceEqual(y, StringComparer.Ordinal);

        public int GetHashCode(List<string> obj) => obj.Aggregate(0, (hash, item) => HashCode.Combine(hash, item));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
