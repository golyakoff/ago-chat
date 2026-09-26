using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Cors;
using Ago.Chat.Api.Hubs;
using Ago.Chat.Api.Realtime;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.UseCases.AcknowledgeMessageDelivered;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.GetConversationHistoryAsSiteConfigureHolder;
using Ago.Chat.Application.UseCases.GetOperatorPresence;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.GetTeamMessageHistory;
using Ago.Chat.Application.UseCases.GetVisitorHistory;
using Ago.Chat.Application.UseCases.GetVisitorPresence;
using Ago.Chat.Application.UseCases.RemoveTeamMessage;
using Ago.Chat.Application.UseCases.ResolveMessageDeliveredDelivery;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.SendTeamMessage;
using Ago.Chat.Application.UseCases.SetOperatorPresence;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Module;
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
/// `25-119`'s own Done-when, the one claim a unit-level assertion could not carry: a visitor's own
/// <c>AcknowledgeDeliveredAsync</c> hub call for a real operator-authored message results in the
/// authoring operator's own live connection receiving the new push - modelled directly on `25-110`'s
/// <see cref="AttachmentUploadGrantLiveDeliveryEndToEndTests"/> (real Kestrel host, real Postgres, real
/// RabbitMQ, real Redis, a real <c>HubConnection</c> with no <c>.WithAutomaticReconnect()</c>).
///
/// <para><b>Both hubs are the real ones - <see cref="VisitorHub"/> and <see cref="OperatorHub"/> - not a
/// stand-in for either.</b> An earlier draft of this file tried a small purpose-built echo hub for the
/// operator side, the same shape <see cref="ActiveSiteHubResolutionTests"/> uses to isolate a transport
/// claim from unrelated business wiring. It failed: the live push never reached that connection, because
/// <c>SignalRConnectionDispatcher</c>/<c>ILocalConnectionDispatcher</c> resolves a connection through the
/// concrete hub type it actually connected to (SignalR's own per-hub-type <c>HubLifetimeManager</c>), not
/// by connection id alone - a connection registered under <see cref="PrincipalKeys.ForOperator"/> is
/// invisible to a delivery mechanism addressed at a different hub type than the one it is actually
/// connected to. <see cref="ActiveSiteHubResolutionTests"/>'s own stand-in never had to clear that bar:
/// it tests a hub method's own authorization/claims resolution, never a push arriving through the shared
/// dispatcher. Since operator authentication in production is now real Keycloak OIDC with no lightweight
/// dev-issued token (`JwtTokenService.IssueVisitorToken`'s own remarks: "the Operator scheme's own
/// counterpart... was deleted in `5-05` - `adr/0022` replaces it outright with real OIDC, never evolves
/// it"), this test instead registers its own self-signed backing for the *same* <c>JwtSchemes.Operator</c>
/// scheme name <see cref="OperatorHub"/> already declares - the scheme is just a name; what
/// <c>[Authorize(AuthenticationSchemes = JwtSchemes.Operator, Policy = "RequireOperatorIdentity")]</c>
/// actually needs is a principal carrying <c>AgoClaimTypes.OperatorId</c>, which a locally-signed JWT
/// supplies exactly as well as a Keycloak-issued one for this file's own narrow purpose. Every other
/// dependency <see cref="OperatorHub"/>'s constructor needs is wired for real, against the same Postgres
/// this fixture already runs, the identical "exists in the DI graph, mostly never invoked" shape
/// <see cref="AttachmentUploadGrantLiveDeliveryEndToEndTests"/> already uses for <see cref="VisitorHub"/>'s
/// own unrelated dependencies.</para>
/// </summary>
[Collection(ConnectionFanoutCollection.Name)]
public sealed class MessageDeliveredLiveDeliveryEndToEndTests(ConnectionFanoutFixture fixture)
{
    private const string VisitorTokenIssuer = "ago-chat-api-test";
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task AVisitorsAcknowledgeDeliveredCall_ReachesTheAuthoringOperatorsLiveConnection_WithTheMessageIdAndTimestamp()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var messageId = new MessageId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            // OperatorHub.OnConnectedAsync calls SetOperatorPresenceHandler.NoteConnectedAsync, which
            // reads the operator row back through IPermissionChecker's own site scope - no permission is
            // actually exercised by this test, but a role row keeps that read from tripping over an
            // empty roster the way ActiveSiteHubResolutionTests' own remarks warn a stripped-down host can.
            seed.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [] });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await seed.SaveChangesAsync(CancellationToken.None);

            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
            // visitor's own real first message before AssignTo, which still only accepts Waiting.
            conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            conversation.AssignTo(operatorId, Now);
            conversation.AddOperatorMessage(operatorId, messageId, new MessageBody("how can I help?"), Now);
            await new ConversationRepository(seed).SaveAsync(conversation, CancellationToken.None);
        }

        var visitorSigningKeys = TestSigningKeys.Ring();
        var visitorToken = new JwtTokenService(visitorSigningKeys, VisitorTokenIssuer, new SystemClock())
            .IssueVisitorToken(visitorId, siteId);
        var operatorSigningKeys = TestSigningKeys.Ring();
        var operatorToken = IssueOperatorTestToken(operatorSigningKeys, operatorId);

        var nodeUnderTest = new NodeId($"node-under-test-{Guid.NewGuid():N}");
        await using var host = await BuildHostAsync(visitorSigningKeys, operatorSigningKeys, nodeUnderTest);

        // The operator's own live connection - held open the whole time, never reconnected (no
        // .WithAutomaticReconnect() - AttachmentUploadGrantLiveDeliveryEndToEndTests' own remarks
        // explain why that is what makes "never reconnects" true by construction).
        await using var operatorConnection = new HubConnectionBuilder()
            .WithUrl($"{host.BaseUrl}hubs/operator", options => { options.AccessTokenProvider = () => Task.FromResult(operatorToken)!; })
            .Build();

        var received = new List<MessageDeliveredDto>();
        var receivedSignal = new SemaphoreSlim(0);
        operatorConnection.On<MessageDeliveredDto>("MessageDelivered", dto =>
        {
            received.Add(dto);
            receivedSignal.Release();
        });
        Exception? closedException = null;
        operatorConnection.Closed += ex =>
        {
            closedException = ex;
            return Task.CompletedTask;
        };

        await operatorConnection.StartAsync();
        Assert.Equal(HubConnectionState.Connected, operatorConnection.State);

        // The visitor's own real connection, calling the real VisitorHub.AcknowledgeDeliveredAsync -
        // never calls JoinAsync (this test seeds the conversation and its message directly, matching
        // every other fanout end-to-end test's own seeding style - StartConversationHandler/
        // SendVisitorMessageHandler/GetConversationHistoryHandler only need to exist in the DI graph
        // for VisitorHub's constructor to resolve).
        await using var visitorConnection = new HubConnectionBuilder()
            .WithUrl($"{host.BaseUrl}hubs/visitor", options => { options.AccessTokenProvider = () => Task.FromResult(visitorToken)!; })
            .Build();
        await visitorConnection.StartAsync();
        Assert.Equal(HubConnectionState.Connected, visitorConnection.State);

        // The real handler chain: AcknowledgeMessageDeliveredHandler -> outbox -> OutboxDispatcher ->
        // MessageDelivered -> MessageDeliveredFanoutConsumer -> ResolveMessageDeliveredTargetsHandler ->
        // NodeFanoutPublisher -> NodeDeliveryConsumer -> the real SignalRConnectionDispatcher this same
        // host resolved above.
        await using var fanoutPublisherConnection = fixture.CreateRabbitMqConnection();
        await using var fanoutServices = BuildFanoutServiceProvider(fanoutPublisherConnection);

        await using var dispatcherConnection = fixture.CreateRabbitMqConnection();
        var outboxDispatcher = new OutboxDispatcher(
            fixture.DataSource, new RabbitMqEventPublisher(dispatcherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock(),
            Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromMilliseconds(500) }), NullLogger<OutboxDispatcher>.Instance);

        await using var fanoutConsumerConnection = fixture.CreateRabbitMqConnection();
        var fanoutConsumer = new MessageDeliveredFanoutConsumer(
            new RabbitMqEventConsumer(fanoutConsumerConnection), fanoutServices.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MessageDeliveredFanoutConsumerOptions()), NullLogger<MessageDeliveredFanoutConsumer>.Instance);

        var localDispatcher = host.Services.GetRequiredService<ILocalConnectionDispatcher>();
        await using var nodeConsumerConnection = fixture.CreateRabbitMqConnection();
        var nodeConsumer = new NodeDeliveryConsumer(
            new RabbitMqEventConsumer(nodeConsumerConnection), localDispatcher, nodeUnderTest, NullLogger<NodeDeliveryConsumer>.Instance);

        await outboxDispatcher.StartAsync(CancellationToken.None);
        await fanoutConsumer.StartAsync(CancellationToken.None);
        await nodeConsumer.StartAsync(CancellationToken.None);

        // `15-17`: wait for each Competing subscription's own queue to have a live consumer attached,
        // not merely that the queue exists - see WebhookDispatchSharedQueueRegressionTests' own remarks.
        using var subscriptionManagementClient = fixture.CreateRabbitMqManagementClient();
        await RabbitMqSubscriptionTestHelpers.AwaitAllCompetingSubscriptionsAsync(
            subscriptionManagementClient, TimeSpan.FromSeconds(10),
            (nameof(MessageDelivered), MessageDeliveredFanoutConsumer.ConsumerName),
            (NodeTopics.For(nodeUnderTest), RabbitMqSubscriptionTestHelpers.NodeDeliveryConsumerName));

        try
        {
            // Fails-before proof #2: the live push actually reaches the operator's own connection - not
            // just that the event was enqueued.
            await visitorConnection.InvokeAsync("AcknowledgeDeliveredAsync", conversationId.Value, messageId.Value);

            var sawDelivery = await receivedSignal.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(
                sawDelivery,
                $"Timed out waiting for the authoring operator's live connection to receive the delivery push. "
                + $"operatorConnState={operatorConnection.State}, closedException={closedException}");
            Assert.Equal(HubConnectionState.Connected, operatorConnection.State);
            var push = Assert.Single(received);
            Assert.Equal(conversationId.Value, push.ConversationId);
            Assert.Equal(messageId.Value, push.MessageId);
        }
        finally
        {
            await outboxDispatcher.StopAsync(CancellationToken.None);
            await fanoutConsumer.StopAsync(CancellationToken.None);
            await nodeConsumer.StopAsync(CancellationToken.None);
        }

        // Fails-before proof #1: the ack actually marked DeliveredAt, visible in a subsequent
        // GetConversationHistory read - not merely that a push happened to arrive.
        await using var readDb = fixture.CreateDbContext();
        var readStore = new ConversationReadStore(fixture.DataSource);
        var history = await new GetConversationHistoryHandler(
                new ConversationRepository(readDb), readStore, new PermissionChecker(readDb))
            .HandleAsVisitorAsync(new GetConversationHistoryAsVisitor(conversationId, visitorId, null, 10), CancellationToken.None);
        Assert.True(history.IsSuccess);
        var reloadedMessage = Assert.Single(history.Value.Messages, m => m.Id == messageId);
        Assert.NotNull(reloadedMessage.DeliveredAt);
    }

    /// <summary>This test's own self-signed backing for <c>JwtSchemes.Operator</c> - see this class's
    /// own remarks for why a real Keycloak round trip is out of scope here.</summary>
    private static string IssueOperatorTestToken(VisitorSigningKeyRing signingKeys, OperatorId operatorId)
    {
        var now = DateTimeOffset.UtcNow;
        var token = new JwtSecurityToken(
            issuer: VisitorTokenIssuer,
            audience: JwtSchemes.Operator,
            claims: [new Claim(AgoClaimTypes.OperatorId, operatorId.Value.ToString())],
            notBefore: now.UtcDateTime,
            expires: now.AddHours(1).UtcDateTime,
            signingCredentials: signingKeys.Signing);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private ServiceProvider BuildFanoutServiceProvider(RabbitMqConnection fanoutPublisherConnection)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionRegistry>(_ => new RedisConnectionRegistry(
            fixture.RedisMultiplexer, Options.Create(new ConnectionRegistryOptions()), NullLogger<RedisConnectionRegistry>.Instance));
        services.AddSingleton<IEventPublisher>(_ => new RabbitMqEventPublisher(fanoutPublisherConnection, NullLogger<RabbitMqEventPublisher>.Instance));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<INodeFanoutPublisher, NodeFanoutPublisher>();
        services.AddScoped<ResolveMessageDeliveredTargetsHandler>();
        return services.BuildServiceProvider();
    }

    /// <summary>Never called by anything in this test - exists purely so <see cref="VisitorHub"/>'s own
    /// constructor can resolve. See <see cref="AttachmentUploadGrantLiveDeliveryEndToEndTests"/>'s
    /// identical helper.</summary>
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

    /// <summary>Never called by anything in this test (the operator connection is never dropped) -
    /// exists purely so <see cref="OperatorHub"/>'s own <c>OperatorPresencePublisher</c> dependency can
    /// resolve without a second RabbitMQ connection this test does not otherwise need.</summary>
    private sealed class NoOpEventPublisher : IEventPublisher
    {
        public Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public IServiceProvider Services => App.Services;

        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, mapping the real
    /// <see cref="VisitorHub"/> and the real <see cref="OperatorHub"/> - this class's own remarks explain
    /// why both are the genuine production hubs rather than a stand-in for either.</summary>
    private async Task<TestHost> BuildHostAsync(
        VisitorSigningKeyRing visitorSigningKeys, VisitorSigningKeyRing operatorSigningKeys, NodeId currentNode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddRouting();
        builder.Services.AddSignalR(options => options.EnableDetailedErrors = true);
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
        // `26-108`: OperatorPresencePublisher (resolved by OperatorHub on disconnect below) now also
        // depends on OperatorPresenceLostSuppressionOptions - this hand-built host registers the
        // publisher's deps itself rather than running ChatModule, so it must register this one too.
        builder.Services.AddSingleton(new OperatorPresenceLostSuppressionOptions());
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IVisitorEmojiPairGenerator, VisitorEmojiPairGenerator>();
        builder.Services.AddScoped<IMessagePipeline>(_ => new SynchronousMessagePipeline(fixture.DataSource));
        // `adr/0186` S1: StartConversationHandler's own new constructor dependency - re-adds the
        // registration `26-114`/`adr/0182`'s own comment below removed for a different, now-gone
        // reason. It now needs a real channel-identity lookup again, to resolve the analytics
        // `ConversationOpened` event's own channel label.
        builder.Services.AddScoped<IChannelIdentityRepository, ChannelIdentityRepository>();
        builder.Services.AddScoped<StartConversationHandler>();
        builder.Services.AddScoped<SendVisitorMessageHandler>();
        builder.Services.AddScoped<GetConversationHistoryHandler>();
        builder.Services.AddSingleton<IWidgetActivityRecorder, NoOpWidgetActivityRecorder>();
        // `25-119`: the one handler this host's own test actually exercises.
        builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        builder.Services.AddScoped<AcknowledgeMessageDeliveredHandler>();

        // `25-119`: OperatorHub's own remaining dependencies - every one wired against this same
        // Postgres, mostly never invoked by this test (only OnConnectedAsync ever runs), the identical
        // "exists in the DI graph, mostly unused" shape this file already uses for VisitorHub's own
        // unrelated dependencies (StartConversationHandler and friends, above).
        builder.Services.AddScoped<IConversationAssignmentLog, ConversationAssignmentLog>();
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        // `25-170`: AssignConversationHandler now also composes IOperatorRoleRepository (a self-seat
        // check against operator_roles).
        builder.Services.AddScoped<IOperatorRoleRepository, OperatorRoleRepository>();
        builder.Services.AddScoped<IOperatorCapacity, OperatorCapacityStore>();
        builder.Services.AddScoped<ISiteSuspensionReadStore>(_ => new SiteSuspensionReadStore(fixture.DataSource));
        // `26-114`/`adr/0182`: `IChannelIdentityRepository` was removed from here - it was only ever
        // for `GetVisitorHistoryHandler`'s own now-removed channel-identity gate - and then re-added
        // above for an unrelated reason (`adr/0186` S1's own comment on `StartConversationHandler`).
        builder.Services.AddScoped<IAccessRecordRepository>(_ => new AccessRecordRepository(fixture.DataSource));
        builder.Services.AddScoped<ITeamChatRepository>(sp => new TeamChatRepository(
            sp.GetRequiredService<AgoChatDbContext>(), fixture.DataSource,
            sp.GetRequiredService<IOutboxWriter>(), sp.GetRequiredService<IIdGenerator>()));
        builder.Services.AddScoped<ITeamMessageReadStore>(_ => new TeamMessageReadStore(fixture.DataSource));
        builder.Services.AddSingleton<IEventPublisher, NoOpEventPublisher>();
        builder.Services.AddSingleton(new ConsoleOriginOptions());
        builder.Services.AddScoped<ConsoleOriginValidator>();
        builder.Services.AddScoped<OperatorPresencePublisher>();
        builder.Services.AddScoped<AssignConversationHandler>();
        builder.Services.AddScoped<SendOperatorMessageHandler>();
        builder.Services.AddScoped<GetVisitorHistoryHandler>();
        // `26-98`: OperatorHub's newest dependency - see this file's own remarks two lines up on why
        // this hand-built host must register every one of the hub's constructor parameters itself.
        builder.Services.AddScoped<GetConversationHistoryAsSiteConfigureHolderHandler>();
        builder.Services.AddScoped<GetVisitorPresenceHandler>();
        builder.Services.AddScoped<SetOperatorPresenceHandler>();
        builder.Services.AddScoped<GetOperatorPresenceHandler>();
        builder.Services.AddScoped<SendTeamMessageHandler>();
        builder.Services.AddScoped<GetTeamMessageHistoryHandler>();
        builder.Services.AddScoped<RemoveTeamMessageHandler>();

        builder.Services.AddSingleton<LocalConnectionTracker>();
        builder.Services.AddSingleton<DrainState>();
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
            })
            // `25-119`: this test's own self-signed backing for JwtSchemes.Operator - see this class's
            // own remarks for why a real Keycloak round trip is out of scope here.
            .AddJwtBearer(JwtSchemes.Operator, options =>
            {
                options.MapInboundClaims = false;
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/operator"))
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
                    ValidAudience = JwtSchemes.Operator,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = (_, _, _, _) => operatorSigningKeys.ValidationKeys(),
                    ValidateLifetime = true,
                };
            });
        builder.Services.AddAuthorization(options =>
        {
            // `5-05`: the same policy OperatorHub's own [Authorize] attribute names - a Keycloak-issued
            // token can validate and still resolve to no known operator, and this is what rejects that;
            // this test's own self-signed token already carries the claim directly, so the policy is
            // satisfied without OperatorIdentityClaimsTransformation ever running.
            options.AddPolicy(
                "RequireOperatorIdentity",
                policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId));
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<VisitorHub>("/hubs/visitor");
        app.MapHub<OperatorHub>("/hubs/operator");

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
