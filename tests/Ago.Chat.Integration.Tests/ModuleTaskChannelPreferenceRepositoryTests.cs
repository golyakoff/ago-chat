using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `20-11`: proves the one guarantee no fake can stand in for (`testing.md`) - the real storage-level
/// backstops <c>ModuleTaskChannelPreferenceConfiguration</c>'s own remarks describe (a unique
/// <c>(module_task_id, priority)</c> pair and a unique <c>(module_task_id, channel_identity_id)</c>
/// pair), plus <see cref="ModuleTaskChannelPreferenceRepository.ReplaceForModuleTaskAsync"/>'s own real
/// round trip against Postgres.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ModuleTaskChannelPreferenceRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<(SiteId Site, VisitorId Visitor, ModuleTaskId ModuleTask, ChannelIdentityId First, ChannelIdentityId Second)>
        SeedSiteVisitorTaskAndTwoIdentitiesAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
        var task = conversation.StartModuleTask(
            new ModuleTaskId(Guid.NewGuid()), new ModuleKey("booking-flow"), "ext-1", Now, null, null, []);
        db.Conversations.Add(conversation);
        var first = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Telegram, new ExternalChannelAddress($"tg-{Guid.NewGuid():N}"), visitorId, Now);
        var second = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Max, new ExternalChannelAddress($"max-{Guid.NewGuid():N}"), visitorId, Now);
        db.ChannelIdentities.Add(first);
        db.ChannelIdentities.Add(second);
        await db.SaveChangesAsync();

        return (siteId, visitorId, task.Id, first.Id, second.Id);
    }

    [Fact]
    public async Task ReplaceForModuleTaskAsync_ThenListForModuleTaskAsync_RoundTripsInPriorityOrder()
    {
        var (siteId, visitorId, moduleTaskId, first, second) = await SeedSiteVisitorTaskAndTwoIdentitiesAsync();
        var rows = new List<ModuleTaskChannelPreference>
        {
            ModuleTaskChannelPreference.Add(new ModuleTaskChannelPreferenceId(Guid.NewGuid()), siteId, moduleTaskId, visitorId, second, 1, Now),
            ModuleTaskChannelPreference.Add(new ModuleTaskChannelPreferenceId(Guid.NewGuid()), siteId, moduleTaskId, visitorId, first, 2, Now),
        };

        await using (var db = fixture.CreateDbContext())
        {
            await new ModuleTaskChannelPreferenceRepository(db).ReplaceForModuleTaskAsync(moduleTaskId, rows, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var loaded = await new ModuleTaskChannelPreferenceRepository(readDb).ListForModuleTaskAsync(moduleTaskId, CancellationToken.None);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(second, loaded[0].ChannelIdentityId);
        Assert.Equal(1, loaded[0].Priority);
        Assert.Equal(first, loaded[1].ChannelIdentityId);
        Assert.Equal(2, loaded[1].Priority);
    }

    /// <summary>The real reason <c>ReplaceForModuleTaskAsync</c> is two <c>SaveChangesAsync</c> calls,
    /// not one (the repository's own remarks) - replacing a list with one that reuses the identical
    /// priority values as the old one must not trip <c>ux_module_task_channel_preferences_module_task_priority</c>
    /// on the way through. A single batched delete+insert would risk exactly this race.</summary>
    [Fact]
    public async Task ReplaceForModuleTaskAsync_WithTheIdenticalPrioritiesAsTheOldList_Succeeds()
    {
        var (siteId, visitorId, moduleTaskId, first, second) = await SeedSiteVisitorTaskAndTwoIdentitiesAsync();
        var original = new List<ModuleTaskChannelPreference>
        {
            ModuleTaskChannelPreference.Add(new ModuleTaskChannelPreferenceId(Guid.NewGuid()), siteId, moduleTaskId, visitorId, first, 1, Now),
        };
        await using (var db = fixture.CreateDbContext())
        {
            await new ModuleTaskChannelPreferenceRepository(db).ReplaceForModuleTaskAsync(moduleTaskId, original, CancellationToken.None);
        }

        // Same priority (1), a different channel identity - the exact shape a re-priority of an
        // existing list produces.
        var replacement = new List<ModuleTaskChannelPreference>
        {
            ModuleTaskChannelPreference.Add(new ModuleTaskChannelPreferenceId(Guid.NewGuid()), siteId, moduleTaskId, visitorId, second, 1, Now),
        };
        await using (var db = fixture.CreateDbContext())
        {
            await new ModuleTaskChannelPreferenceRepository(db).ReplaceForModuleTaskAsync(moduleTaskId, replacement, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var loaded = await new ModuleTaskChannelPreferenceRepository(readDb).ListForModuleTaskAsync(moduleTaskId, CancellationToken.None);
        var row = Assert.Single(loaded);
        Assert.Equal(second, row.ChannelIdentityId);
    }

    [Fact]
    public async Task ReplaceForModuleTaskAsync_WithAnEmptyList_ClearsAnExistingList()
    {
        var (siteId, visitorId, moduleTaskId, first, _) = await SeedSiteVisitorTaskAndTwoIdentitiesAsync();
        var rows = new List<ModuleTaskChannelPreference>
        {
            ModuleTaskChannelPreference.Add(new ModuleTaskChannelPreferenceId(Guid.NewGuid()), siteId, moduleTaskId, visitorId, first, 1, Now),
        };
        await using (var db = fixture.CreateDbContext())
        {
            await new ModuleTaskChannelPreferenceRepository(db).ReplaceForModuleTaskAsync(moduleTaskId, rows, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            await new ModuleTaskChannelPreferenceRepository(db).ReplaceForModuleTaskAsync(moduleTaskId, [], CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var loaded = await new ModuleTaskChannelPreferenceRepository(readDb).ListForModuleTaskAsync(moduleTaskId, CancellationToken.None);
        Assert.Empty(loaded);
    }

    /// <summary>The storage-level backstop for "1-based, unique within one booking" -
    /// <c>ModuleTaskChannelPreferenceConfiguration</c>'s own remarks on why this exists even though
    /// <c>SetModuleTaskChannelPriorityListHandler</c> never produces a duplicate priority itself. A direct
    /// <c>SaveChangesAsync</c> (bypassing the repository's own replace semantics) is what proves the
    /// index, not the repository.</summary>
    [Fact]
    public async Task TwoRowsForTheSameModuleTaskAndPriority_ViolatesTheUniqueIndex()
    {
        var (siteId, visitorId, moduleTaskId, first, second) = await SeedSiteVisitorTaskAndTwoIdentitiesAsync();

        await using var db = fixture.CreateDbContext();
        db.ModuleTaskChannelPreferences.Add(
            ModuleTaskChannelPreference.Add(new ModuleTaskChannelPreferenceId(Guid.NewGuid()), siteId, moduleTaskId, visitorId, first, 1, Now));
        await db.SaveChangesAsync();

        db.ModuleTaskChannelPreferences.Add(
            ModuleTaskChannelPreference.Add(new ModuleTaskChannelPreferenceId(Guid.NewGuid()), siteId, moduleTaskId, visitorId, second, 1, Now));

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
    }

    /// <summary>The storage-level backstop for "the same channel identity cannot appear twice in one
    /// booking's own list".</summary>
    [Fact]
    public async Task TwoRowsForTheSameModuleTaskAndChannelIdentity_ViolatesTheUniqueIndex()
    {
        var (siteId, visitorId, moduleTaskId, first, _) = await SeedSiteVisitorTaskAndTwoIdentitiesAsync();

        await using var db = fixture.CreateDbContext();
        db.ModuleTaskChannelPreferences.Add(
            ModuleTaskChannelPreference.Add(new ModuleTaskChannelPreferenceId(Guid.NewGuid()), siteId, moduleTaskId, visitorId, first, 1, Now));
        await db.SaveChangesAsync();

        db.ModuleTaskChannelPreferences.Add(
            ModuleTaskChannelPreference.Add(new ModuleTaskChannelPreferenceId(Guid.NewGuid()), siteId, moduleTaskId, visitorId, first, 2, Now));

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
    }
}
