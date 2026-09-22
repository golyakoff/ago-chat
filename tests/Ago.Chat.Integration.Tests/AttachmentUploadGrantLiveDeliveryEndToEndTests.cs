using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Cors;
using Ago.Chat.Api.Hubs;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.GrantAttachmentUpload;
using Ago.Chat.Application.UseCases.ResolveAttachmentUploadGrantDelivery;
using Ago.Chat.Application.UseCases.RevokeAttachmentUpload;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Persistence.Postgres;
using Ago.Platform.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Polly;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-110`'s own Done-when, the one claim in the item that a unit-level assertion could not carry:
/// "a visitor holding the conversation open the whole time sees the attach icon appear or disappear
/// within a normal message-delivery latency of the operator's own click - no reload, no reconnect
/// needed." Every other fan-out proof in this suite
/// (<see cref="ConnectionFanoutEndToEndTests"/>, <see cref="ConversationAssignmentFanoutEndToEndTests"/>)
/// stops at a hand-written <c>ILocalConnectionDispatcher</c> fake standing in for the SignalR edge -
/// correct for proving the *fan-out* mechanism, but this item's own claim is specifically about a real,
/// held-open <c>HubConnection</c> actually receiving the push, which a fake dispatcher cannot answer.
/// This test goes one hop further: a real Kestrel host (not <c>TestServer</c> - the same
/// "WebSocket upgrade" reasoning <see cref="ActiveSiteHubResolutionTests"/>'s own remarks give for why
/// that matters) mapping the real <see cref="VisitorHub"/>, behind the real
/// <see cref="SignalRConnectionDispatcher"/>, reached by a real <c>Microsoft.AspNetCore.SignalR.Client.HubConnection</c>
/// - the actual widget-side transport, not a stand-in for it.
///
/// <para><b>Real Postgres</b> (the grant/revoke write and its outbox row, through the actual production
/// <see cref="GrantAttachmentUploadHandler"/>/<see cref="RevokeAttachmentUploadHandler"/> - nothing about
/// the write path is stubbed), <b>real RabbitMQ</b> (<c>OutboxDispatcher</c> -&gt;
/// <see cref="AttachmentUploadGrantFanoutConsumer"/> -&gt;
/// <see cref="ResolveAttachmentUploadGrantDeliveryTargetsHandler"/> -&gt; <see cref="NodeFanoutPublisher"/>
/// -&gt; <see cref="NodeDeliveryConsumer"/>, the identical chain <c>Ago.Chat.Worker</c> runs in
/// production), <b>real Redis</b> (<see cref="RedisConnectionRegistry"/>, exactly where
/// <see cref="HubConnectionRegistration.OnConnectedAsync"/> records the connection this test's own
/// <c>HubConnection</c> just opened) - the same three-resource combination
/// <see cref="ConnectionFanoutFixture"/> already exists for.</para>
///
/// <para><b>Never calls <c>JoinAsync</c>.</b> <see cref="VisitorHub.OnConnectedAsync"/> already registers
/// the connection's principal (<c>HubConnectionRegistration.OnConnectedAsync</c>) the instant the socket
/// connects - independent of the hub's own join method, which only starts/resumes a conversation and
/// replays history. This test seeds the conversation directly in Postgres instead (matching every other
/// fanout end-to-end test's own seeding style), so <see cref="StartConversationHandler"/>/
/// <see cref="SendVisitorMessageHandler"/>/<see cref="GetConversationHistoryHandler"/> only need to exist
/// in the DI graph for <see cref="VisitorHub"/>'s constructor to resolve - they are never invoked.</para>
///
/// <para><b>Never configures automatic reconnect.</b> No <c>.WithAutomaticReconnect()</c> on the
/// <c>HubConnectionBuilder</c> below - without it, `@microsoft/signalr`'s .NET counterpart has no
/// reconnect mechanism at all, which is what makes "the connection never reconnects" a fact this test
/// does not have to police separately: there is nothing here that could reconnect it.</para>
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class AttachmentUploadGrantLiveDeliveryEndToEndTests(ConnectionFanoutFixture fixture)
{
    private const string VisitorTokenIssuer = "ago-chat-api-test";
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task AVisitorConnectionHeldOpenTheWholeTime_SeesTheGrantAndTheRevokeLive_WithNoReloadOrReconnect()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationAttachmentUploadGrant.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
            // visitor's own real first message before AssignTo, which still only accepts Waiting.
            conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            conversation.AssignTo(operatorId, Now);
            seed.Conversations.Add(conversation);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var visitorSigningKeys = TestSigningKeys.Ring();
        var visitorToken = new JwtTokenService(visitorSigningKeys, VisitorTokenIssuer, new SystemClock())
            .IssueVisitorToken(visitorId, siteId);

        var nodeUnderTest = new NodeId($"node-under-test-{Guid.NewGuid():N}");
        await using var host = await BuildVisitorHostAsync(visitorSigningKeys, nodeUnderTest);

        // The one real SignalR connection standing in for the visitor - held open across everything
        // below, never stopped, never reconnected (no .WithAutomaticReconnect() - this type's own
        // remarks explain why that is what makes "never reconnects" true by construction).
        await using var hubConnection = new HubConnectionBuilder()
            .WithUrl($"{host.BaseUrl}hubs/visitor", options => { options.AccessTokenProvider = () => Task.FromResult(visitorToken)!; })
            .Build();

        var received = new List<AttachmentUploadGrantChangedDto>();
        var receivedSignal = new SemaphoreSlim(0);
        hubConnection.On<AttachmentUploadGrantChangedDto>("AttachmentUploadGrantChanged", dto =>
        {
            received.Add(dto);
            receivedSignal.Release();
        });

        await hubConnection.StartAsync();
        Assert.Equal(HubConnectionState.Connected, hubConnection.State);

        // The real handler chain: GrantAttachmentUploadHandler -> outbox -> OutboxDispatcher ->
        // AttachmentUploadGrantChanged -> AttachmentUploadGrantFanoutConsumer ->
        // ResolveAttachmentUploadGrantDeliveryTargetsHandler -> NodeFanoutPublisher -> NodeDeliveryConsumer
        // -> the real SignalRConnectionDispatcher this same host resolved above.
        await using var fanoutPublisherConnection = fixture.CreateRabbitMqConnection();
        await using var fanoutServices = BuildFanoutServiceProvider(fanoutPublisherConnection);

        await using var dispatcherConnection = fixture.CreateRabbitMqConnection();
        var outboxDispatcher = new OutboxDispatcher(
            fixture.DataSource, new RabbitMqEventPublisher(dispatcherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock(),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromMilliseconds(500) }), NullLogger<OutboxDispatcher>.Instance);

        await using var fanoutConsumerConnection = fixture.CreateRabbitMqConnection();
        var fanoutConsumer = new AttachmentUploadGrantFanoutConsumer(
            new RabbitMqEventConsumer(fanoutConsumerConnection), fanoutServices.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AttachmentUploadGrantFanoutConsumerOptions()), NullLogger<AttachmentUploadGrantFanoutConsumer>.Instance);

        var localDispatcher = host.Services.GetRequiredService<ILocalConnectionDispatcher>();
        await using var nodeConsumerConnection = fixture.CreateRabbitMqConnection();
        var nodeConsumer = new NodeDeliveryConsumer(
            new RabbitMqEventConsumer(nodeConsumerConnection), localDispatcher, nodeUnderTest, NullLogger<NodeDeliveryConsumer>.Instance);

        await outboxDispatcher.StartAsync(CancellationToken.None);
        await fanoutConsumer.StartAsync(CancellationToken.None);
        await nodeConsumer.StartAsync(CancellationToken.None);

        // `15-17`: wait for the fact each Competing subscription's own queue has a live consumer
        // attached, not merely that the queue exists - see WebhookDispatchSharedQueueRegressionTests'
        // own remarks for why StartAsync alone cannot be awaited for this.
        using var subscriptionManagementClient = fixture.CreateRabbitMqManagementClient();
        await RabbitMqSubscriptionTestHelpers.AwaitAllCompetingSubscriptionsAsync(
            subscriptionManagementClient, TimeSpan.FromSeconds(10),
            (nameof(AttachmentUploadGrantChanged), AttachmentUploadGrantFanoutConsumer.ConsumerName),
            (NodeTopics.For(nodeUnderTest), RabbitMqSubscriptionTestHelpers.NodeDeliveryConsumerName));

        try
        {
            // The operator's own grant call - the real production handler, nothing stubbed.
            await using (var writeDb = fixture.CreateDbContext())
            {
                var grantHandler = new GrantAttachmentUploadHandler(
                    new ConversationRepository(writeDb), new ConversationAttachmentUploadGrantRepository(writeDb),
                    new PermissionChecker(writeDb), new EfUnitOfWork(writeDb), new EfOutboxWriter<AgoChatDbContext>(writeDb),
                    new UuidV7Generator(), new SystemClock());
                var grantResult = await grantHandler.HandleAsync(
                    new GrantAttachmentUpload(conversationId, operatorId, siteId), CancellationToken.None);
                Assert.True(grantResult.IsSuccess, grantResult.IsFailure ? grantResult.Error!.Value.Message : null);
            }

            var sawGrant = await receivedSignal.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(sawGrant, "Timed out waiting for the live-connected visitor to receive the grant push.");
            // The claim this test exists to prove: still Connected, never Reconnecting/Disconnected -
            // the push arrived on the one connection that was open the whole time.
            Assert.Equal(HubConnectionState.Connected, hubConnection.State);
            var grantPush = Assert.Single(received);
            Assert.True(grantPush.Granted);
            Assert.Equal(conversationId.Value, grantPush.ConversationId);

            // Same connection, still open, no reload - the operator's own revoke call now. `25-110`'s
            // own "revoke matters as much as grant".
            await using (var writeDb = fixture.CreateDbContext())
            {
                var revokeHandler = new RevokeAttachmentUploadHandler(
                    new ConversationRepository(writeDb), new ConversationAttachmentUploadGrantRepository(writeDb),
                    new PermissionChecker(writeDb), new EfUnitOfWork(writeDb), new EfOutboxWriter<AgoChatDbContext>(writeDb),
                    new UuidV7Generator(), new SystemClock());
                var revokeResult = await revokeHandler.HandleAsync(
                    new RevokeAttachmentUpload(conversationId, operatorId, siteId), CancellationToken.None);
                Assert.True(revokeResult.IsSuccess, revokeResult.IsFailure ? revokeResult.Error!.Value.Message : null);
            }

            var sawRevoke = await receivedSignal.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(sawRevoke, "Timed out waiting for the live-connected visitor to receive the revoke push.");
            Assert.Equal(HubConnectionState.Connected, hubConnection.State);
            Assert.Equal(2, received.Count);
            Assert.False(received[1].Granted);
            Assert.Equal(conversationId.Value, received[1].ConversationId);
        }
        finally
        {
            await outboxDispatcher.StopAsync(CancellationToken.None);
            await fanoutConsumer.StopAsync(CancellationToken.None);
            await nodeConsumer.StopAsync(CancellationToken.None);
        }
    }

    private ServiceProvider BuildFanoutServiceProvider(RabbitMqConnection fanoutPublisherConnection)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionRegistry>(_ => new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance));
        // Not IAsyncDisposable-registered here: RabbitMqEventPublisher.DisposeAsync only disposes its
        // own channel, never the RabbitMqConnection it was given - that connection's lifetime is the
        // test method's own `await using` on fanoutPublisherConnection.
        services.AddSingleton<IEventPublisher>(_ => new RabbitMqEventPublisher(fanoutPublisherConnection, NullLogger<RabbitMqEventPublisher>.Instance));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<INodeFanoutPublisher, NodeFanoutPublisher>();
        services.AddScoped<ResolveAttachmentUploadGrantDeliveryTargetsHandler>();
        return services.BuildServiceProvider();
    }

    /// <summary>Never called by anything in this test - it exists purely so <see cref="VisitorHub"/>'s
    /// own constructor can resolve. See this class's own remarks.</summary>
    private sealed class NoOpWidgetActivityRecorder : IWidgetActivityRecorder
    {
        public void RecordLoad(SiteId siteId, DateTimeOffset now)
        {
        }

        public void RecordOpen(SiteId siteId, DateTimeOffset now)
        {
        }

        public void RecordConversation(SiteId siteId, DateTimeOffset now)
        {
        }
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public IServiceProvider Services => App.Services;

        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, mapping the real
    /// <see cref="VisitorHub"/> behind the real <see cref="SignalRConnectionDispatcher"/> - deliberately
    /// not <c>TestServer</c>, this class's own remarks explain why. Every dependency
    /// <see cref="VisitorHub"/>'s own constructor needs is registered against real Postgres/Redis so the
    /// hub actually activates; <see cref="StartConversationHandler"/>/<see cref="SendVisitorMessageHandler"/>/
    /// <see cref="GetConversationHistoryHandler"/> are never invoked by this test (it never calls
    /// <c>JoinAsync</c>/<c>SendMessageAsync</c>/<c>GetHistoryAsync</c>), so nothing about their own
    /// correctness is asserted here - only that the hub they help construct can connect at all.</summary>
    private async Task<TestHost> BuildVisitorHostAsync(VisitorSigningKeyRing visitorSigningKeys, NodeId currentNode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddRouting();
        builder.Services.AddSignalR();
        builder.Services.AddDbContext<AgoChatDbContext>(options => options.UseNpgsql(fixture.DataSource));

        builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
        builder.Services.AddScoped<IVisitorRepository, VisitorRepository>();
        builder.Services.AddScoped<IVisitorRestrictionRepository>(_ => new VisitorRestrictionRepository(fixture.DataSource));
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<IConversationReadStore>(_ => new ConversationReadStore(fixture.DataSource));
        builder.Services.AddSingleton<ICache>(_ => new RedisCache(
            fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisCache>.Instance));
        builder.Services.AddScoped<GetSiteConfigByIdHandler>();

        builder.Services.AddSingleton<IRateLimiter, FakeRateLimiter>();
        builder.Services.AddSingleton(new ConversationCreateRateLimitOptions());
        builder.Services.AddSingleton(new MessageSendRateLimitOptions());
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IVisitorEmojiPairGenerator, VisitorEmojiPairGenerator>();
        builder.Services.AddScoped<IMessagePipeline>(_ => new SynchronousMessagePipeline(fixture.DataSource));
        builder.Services.AddScoped<StartConversationHandler>();
        builder.Services.AddScoped<SendVisitorMessageHandler>();
        builder.Services.AddScoped<GetConversationHistoryHandler>();
        builder.Services.AddSingleton<IWidgetActivityRecorder, NoOpWidgetActivityRecorder>();
        // `25-119`: VisitorHub's own new dependency - not exercised by this test (it never calls
        // AcknowledgeDeliveredAsync), but SignalR activates the whole Hub via this container per
        // invocation, so every constructor parameter must resolve regardless of which method is called.
        builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        builder.Services.AddScoped<Application.UseCases.AcknowledgeMessageDelivered.AcknowledgeMessageDeliveredHandler>();

        // 3-01/3-02's own hub wiring - the real pieces CompositionRoot.ConfigureServices registers for
        // Ago.Chat.Api, hand-wired here the same way ConnectionFanoutEndToEndTests hand-wires the
        // Worker-side half of the same feature, so this host really is what production runs, not a
        // rebuilt stand-in of it.
        builder.Services.AddSingleton<LocalConnectionTracker>();
        builder.Services.AddSingleton<DrainState>();
        // NodeId is a value type - the generic AddSingleton<TService>(TService) overload requires a
        // reference type, so this goes through the non-generic Type-keyed one instead.
        builder.Services.AddSingleton(typeof(NodeId), currentNode);
        builder.Services.AddSingleton<IConnectionRegistry>(_ => new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance));
        builder.Services.AddSingleton<HubConnectionRegistration>();
        builder.Services.AddScoped<HubOriginValidator>();
        builder.Services.AddSingleton<ILocalConnectionDispatcher, SignalRConnectionDispatcher>();

        builder.Services.AddAuthentication()
            .AddJwtBearer(JwtSchemes.Visitor, options =>
            {
                options.MapInboundClaims = false;
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/visitor"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },
                };
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = VisitorTokenIssuer,
                    ValidateAudience = true,
                    ValidAudience = JwtSchemes.Visitor,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = (_, _, _, _) => visitorSigningKeys.ValidationKeys(),
                    ValidateLifetime = true,
                };
            });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<VisitorHub>("/hubs/visitor");

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        var baseUrl = addresses.Addresses.First();
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return new TestHost(app, baseUrl);
    }
}
