using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Polly;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `4-03`'s fail-closed design claim, forced to happen for real: with Redis unreachable,
/// <see cref="RedisLockAssignmentClaimer"/> must assign nothing - not throw, and never a silent
/// fall-through to unlocked claiming (`RedisDistributedLock.TryAcquireAsync` already proves this at
/// the lock level; this proves the claimer built on it inherits the same behaviour end to end,
/// leaving the conversation genuinely `Waiting` and the operator's capacity genuinely untouched).
/// Its own, non-shared Redis container so stopping it cannot affect any other test.
/// </summary>
public sealed class RedisLockAssignmentContainerFailureTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AssignWaitingConversationsAsync_AgainstAStoppedRedis_AssignsNothing()
    {
        var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var redis = new RedisBuilder("redis:7-alpine").Build();
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync());

        await using var dataSource = new NpgsqlDataSourceBuilder(postgres.GetConnectionString()).Build();
        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(dataSource).Options;
        await using (var migrate = new AgoChatDbContext(dbOptions))
        {
            await migrate.Database.MigrateAsync();
        }

        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        await using (var db = new AgoChatDbContext(dbOptions))
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await db.SaveChangesAsync();
        }

        var redisConfiguration = ConfigurationOptions.Parse(redis.GetConnectionString());
        redisConfiguration.ConnectTimeout = 2000;
        redisConfiguration.SyncTimeout = 2000;
        redisConfiguration.AbortOnConnectFail = false;
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConfiguration);
        var resilience = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromMilliseconds(500)).Build();
        var redisLock = new RedisDistributedLock(multiplexer, resilience, NullLogger<RedisDistributedLock>.Instance);
        var claimer = new RedisLockAssignmentClaimer(redisLock, dataSource, new SystemClock(), new UuidV7Generator());

        await redis.StopAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var assignedCount = -1;
        var exception = await Record.ExceptionAsync(async () =>
        {
            assignedCount = await claimer.AssignWaitingConversationsAsync(siteId, batchSize: 10, cts.Token);
        });

        Assert.Null(exception);
        Assert.False(cts.IsCancellationRequested, "Should have returned on its own, not because the test's timeout fired.");
        Assert.Equal(0, assignedCount);

        await using var verify = new AgoChatDbContext(dbOptions);
        var conversation = await verify.Conversations.FindAsync(conversationId);
        Assert.Equal(ConversationState.Waiting, conversation!.State);
        Assert.Null(conversation.OperatorId);

        await using var readConnection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT active_chats FROM operators WHERE id = @id", readConnection);
        command.Parameters.AddWithValue("id", operatorId.Value);
        Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);

        await postgres.DisposeAsync();
        await redis.DisposeAsync();
    }
}
