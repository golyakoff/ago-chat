using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Conversations;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ExportConversation;
using Ago.Chat.Application.UseCases.ExportVisitor;
using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Application.UseCases.GetAllConversationsForSite;
using Ago.Chat.Application.UseCases.GetModuleFlowReportForSite;
using Ago.Chat.Application.UseCases.GetConversationById;
using Ago.Chat.Application.UseCases.GetConversationOutcome;
using Ago.Chat.Application.UseCases.GetChannelDeliveriesForConversation;
using Ago.Chat.Application.UseCases.GetConversionReportForSite;
using Ago.Chat.Application.UseCases.GetOperatorAnalyticsForSite;
using Ago.Chat.Application.UseCases.GetOperatorQueue;
using Ago.Chat.Application.UseCases.GetOwnAnalyticsForOperator;
using Ago.Chat.Application.UseCases.GetTagBreakdownReportForSite;
using Ago.Chat.Application.UseCases.GetVisitorHistory;
using Ago.Chat.Application.UseCases.MarkConversationRead;
using Ago.Chat.Application.UseCases.RequestConversationErasure;
using Ago.Chat.Application.UseCases.SearchConversations;
using Ago.Chat.Application.UseCases.SetConversationOutcome;
using Ago.Chat.Application.UseCases.TransferConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `5-15` over real HTTP, against a real Postgres: the production
/// <c>ConversationsEndpoints.MapConversationsEndpoints</c> mapping, the production handler, and
/// <c>ErrorExtensions</c>'s own status-code translation - so "an operator who is not assigned cannot
/// clear someone else's count" is proven as a genuine <c>403</c> with an RFC 7807 body, not as a
/// <c>Result</c> the endpoint might have chosen to render some other way.
///
/// <para><b>Why a stub authentication scheme</b> rather than <see cref="OperatorOidcFixture"/>'s real
/// Keycloak: this file's subject is authorization on a conversation, and it needs two *different*
/// operator identities to have anything to test. Provisioning a second Keycloak user would prove
/// nothing extra here - that a real token resolves to a real `operator_id` claim is already
/// <c>OperatorOidcAuthenticationTests</c>/<c>KeycloakIdentityPolicyTests</c>' subject. Everything
/// downstream of the claim, including the <c>RequireOperatorIdentity</c> policy itself, is the
/// production wiring.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class MarkConversationReadEndpointTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task TheAssignedOperator_ClearsTheCount_AndItStaysClearedOnReload()
    {
        var seed = await SeedAsync(visitorMessages: 3);

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, seed.AssignedOperatorId, seed.SiteId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/conversations/{seed.ConversationId.Value}/read",
            new ConversationsEndpoints.MarkConversationReadRequest(UpToSequence: 3));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MarkConversationReadResult>();
        Assert.NotNull(body);
        Assert.Equal(0, body.OperatorUnreadCount);
        Assert.Equal(3, body.OperatorLastReadSequence);

        // The point of the whole item: the clear is durable, not a client-side illusion. Read back
        // through a brand new DbContext, exactly what a reloaded console's queue query would see.
        await using var verify = fixture.CreateDbContext();
        var row = await verify.Conversations.SingleAsync(c => c.Id == seed.ConversationId, CancellationToken.None);
        Assert.Equal(0, row.OperatorUnreadCount);
        Assert.Equal(3, row.OperatorLastReadSequence);
    }

    [Fact]
    public async Task AnOperatorWhoIsNotAssigned_Gets403_AndTheCountIsUntouched()
    {
        var seed = await SeedAsync(visitorMessages: 3);

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, seed.OtherOperatorId, seed.SiteId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/conversations/{seed.ConversationId.Value}/read",
            new ConversationsEndpoints.MarkConversationReadRequest(UpToSequence: 3));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // api-design.md: clients branch on `type`, never on the message.
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("Conversation.Forbidden", problem!.Type);

        await using var verify = fixture.CreateDbContext();
        var row = await verify.Conversations.SingleAsync(c => c.Id == seed.ConversationId, CancellationToken.None);
        Assert.Equal(3, row.OperatorUnreadCount);
        Assert.Equal(0, row.OperatorLastReadSequence);
    }

    [Fact]
    public async Task MarkingAnAlreadyReadConversationRead_IsANoOpNotAnError()
    {
        var seed = await SeedAsync(visitorMessages: 2);

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, seed.AssignedOperatorId, seed.SiteId);
        var route = $"/api/v1/conversations/{seed.ConversationId.Value}/read";
        var request = new ConversationsEndpoints.MarkConversationReadRequest(UpToSequence: 2);

        var first = await client.PostAsJsonAsync(route, request);
        var second = await client.PostAsJsonAsync(route, request);
        var third = await client.PostAsJsonAsync(route, request);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        var body = await third.Content.ReadFromJsonAsync<MarkConversationReadResult>();
        Assert.Equal(0, body!.OperatorUnreadCount);
        Assert.Equal(2, body.OperatorLastReadSequence);
    }

    [Fact]
    public async Task AMessageArrivingAfterTheClear_CountsAgain()
    {
        // The badge is not muted by having been read once - the watermark only covers what was seen.
        var seed = await SeedAsync(visitorMessages: 1);

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, seed.AssignedOperatorId, seed.SiteId);
        await client.PostAsJsonAsync(
            $"/api/v1/conversations/{seed.ConversationId.Value}/read",
            new ConversationsEndpoints.MarkConversationReadRequest(UpToSequence: 1));

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ConversationRepository(db);
            var conversation = await repository.GetByIdAsync(seed.ConversationId, CancellationToken.None);
            var message = conversation!.AddVisitorMessage(
                seed.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("one more"), Now);
            conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, message.Sequence);
            conversation.ClearDomainEvents();
            await repository.SaveAsync(conversation, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var row = await verify.Conversations.SingleAsync(c => c.Id == seed.ConversationId, CancellationToken.None);
        Assert.Equal(1, row.OperatorUnreadCount);
    }

    private sealed record ProblemBody(string Type, string Title, int Status);

    private sealed record SeedResult(
        SiteId SiteId, VisitorId VisitorId, OperatorId AssignedOperatorId, OperatorId OtherOperatorId, ConversationId ConversationId);

    private async Task<SeedResult> SeedAsync(int visitorMessages)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var assignedOperatorId = new OperatorId(Guid.NewGuid());
        var otherOperatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
        seed.Operators.Add(new Operator(assignedOperatorId, siteId, OperatorStatus.Online, capacity: 5));
        seed.Operators.Add(new Operator(otherOperatorId, siteId, OperatorStatus.Online, capacity: 5));
        seed.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Operator",
            Permissions = [Permission.ConversationRead.Value],
        });
        // Both operators hold the same role: the 403 under test must come from the per-conversation
        // check, not from a missing permission - otherwise the test would pass without proving
        // anything about assignment at all.
        seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = assignedOperatorId, RoleId = roleId });
        seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = otherOperatorId, RoleId = roleId });

        var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
        conversation.AssignTo(assignedOperatorId, Now);
        for (var i = 0; i < visitorMessages; i++)
        {
            var message = conversation.AddVisitorMessage(
                visitorId, new MessageId(Guid.NewGuid()), new MessageBody("incoming"), Now);
            conversation.IncrementUnreadCount(MessageAuthorKind.Visitor, message.Sequence);
        }

        conversation.ClearDomainEvents();
        seed.Conversations.Add(conversation);

        await seed.SaveChangesAsync(CancellationToken.None);
        return new SeedResult(siteId, visitorId, assignedOperatorId, otherOperatorId, conversationId);
    }

    private static HttpClient CreateClient(WebApplication host, OperatorId operatorId, SiteId siteId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(StubOperatorAuthHandler.OperatorIdHeader, operatorId.Value.ToString());
        client.DefaultRequestHeaders.Add(StubOperatorAuthHandler.SiteIdHeader, siteId.Value.ToString());
        return client;
    }

    private async Task<WebApplication> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
        builder.Services.AddScoped<IConversationReadStore, ConversationReadStore>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<MarkConversationReadHandler>();
        // The other handlers `MapConversationsEndpoints` references. Registered even though no
        // test here calls their routes, and not optional: minimal APIs infer an unregistered complex
        // parameter as a *body* parameter, so leaving `GetOperatorQueueHandler` out makes the GET
        // routes fail to build at all ("Body was inferred but the method does not allow inferred body
        // parameters") and takes the whole test host down with them. Found by doing exactly that.
        // `16-02` adds two more to this same list for the identical reason - GetConversationByIdHandler
        // is itself the *second* GET route this file's own comment already warned about, found live
        // exactly the way this comment predicts.
        builder.Services.AddScoped<GetOperatorQueueHandler>();
        builder.Services.AddScoped<GetAllConversationsForSiteHandler>();
        builder.Services.AddScoped<CloseConversationHandler>();
        builder.Services.AddScoped<RequestConversationErasureHandler>();
        builder.Services.AddScoped<GetConversationByIdHandler>();
        builder.Services.AddScoped<IErasureRequestRepository, ErasureRequestRepository>();
        // `24-11`: same reason again - MapConversationsEndpoints now also maps
        // POST .../exports and POST .../visitor-export, whose ExportConversationHandler/
        // ExportVisitorHandler parameters, and the PersonExportRateLimitOptions parameter their
        // endpoint methods take directly, must all resolve as registered services or the whole
        // route table fails to build - found live exactly the way this comment predicts.
        builder.Services.AddScoped<ExportConversationHandler>();
        builder.Services.AddScoped<ExportVisitorHandler>();
        builder.Services.AddSingleton(new PersonExportRateLimitOptions());
        // `18-07`: same reason as the three above it - MapConversationsEndpoints now also maps
        // GET .../visitor-history, whose GetVisitorHistoryHandler parameter must resolve as a
        // registered service or the whole route table fails to build.
        builder.Services.AddScoped<IChannelIdentityRepository, ChannelIdentityRepository>();
        builder.Services.AddScoped<GetVisitorHistoryHandler>();
        // `18-01`: same reason again - MapConversationsEndpoints now also maps GET .../search, whose
        // SearchConversationsHandler parameter must resolve as a registered service.
        builder.Services.AddScoped<IConversationSearchStore, ConversationSearchStore>();
        builder.Services.AddScoped<SearchConversationsHandler>();
        // `18-08`: same reason again - MapConversationsEndpoints now also maps GET .../analytics,
        // whose GetOperatorAnalyticsForSiteHandler parameter must resolve as a registered service.
        builder.Services.AddScoped<IOperatorAnalyticsReadStore, OperatorAnalyticsReadStore>();
        builder.Services.AddScoped<GetOperatorAnalyticsForSiteHandler>();
        // `23-18`: same reason again - MapConversationsEndpoints now also maps GET .../analytics/me,
        // whose GetOwnAnalyticsForOperatorHandler parameter must resolve as a registered service. No
        // test in this file exercises that route.
        builder.Services.AddScoped<GetOwnAnalyticsForOperatorHandler>();
        // `18-14`: same reason again - MapConversationsEndpoints now also maps
        // GET .../module-flow-report, whose GetModuleFlowReportForSiteHandler parameter must
        // resolve as a registered service. No test in this file exercises that route, so the
        // configured module key's actual value is irrelevant here - unlike ChatModule's own
        // production registration, this test host binds no ModuleFlowReport:* configuration at all,
        // so the plain value is constructed directly rather than through IOptions<T> (which would
        // otherwise need a matching config section this host never loads).
        builder.Services.AddScoped<IModuleFlowReadStore, ModuleFlowReadStore>();
        builder.Services.AddSingleton(new ModuleFlowReportOptions { ModuleKey = "test-module" });
        builder.Services.AddScoped<GetModuleFlowReportForSiteHandler>();
        // `18-02`: same reason again - MapConversationsEndpoints now also maps POST .../transfer,
        // whose TransferConversationHandler parameter must resolve as a registered service. Its own
        // dependencies (IOperatorRepository, IOperatorCapacity, IUnitOfWork) do not need registering
        // here - no test in this file exercises /transfer, and minimal API's endpoint-metadata
        // inference only needs the handler type itself recognized as a service, not constructible.
        builder.Services.AddScoped<TransferConversationHandler>();
        // `18-10`: same reason again - MapConversationsEndpoints now also maps GET .../conversion-report
        // and GET/PUT .../outcome, whose three handlers' parameters must resolve as registered services.
        builder.Services.AddScoped<IConversionReportReadStore, ConversionReportReadStore>();
        builder.Services.AddScoped<GetConversionReportForSiteHandler>();
        builder.Services.AddScoped<SetConversationOutcomeHandler>();
        builder.Services.AddScoped<GetConversationOutcomeHandler>();
        // `18-11`: same reason again - MapConversationsEndpoints now also maps
        // GET .../tag-breakdown-report, whose GetTagBreakdownReportForSiteHandler parameter must
        // resolve as a registered service.
        builder.Services.AddScoped<ITagBreakdownReadStore, TagBreakdownReadStore>();
        builder.Services.AddScoped<GetTagBreakdownReportForSiteHandler>();
        // `23-19`: same reason again - MapConversationsEndpoints now also maps GET .../channel-deliveries.
        builder.Services.AddScoped<IChannelDeliveryReadStore, ChannelDeliveryReadStore>();
        builder.Services.AddScoped<GetChannelDeliveriesForConversationHandler>();
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

        builder.Services.AddAuthentication(JwtSchemes.Operator)
            .AddScheme<AuthenticationSchemeOptions, StubOperatorAuthHandler>(JwtSchemes.Operator, _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(
                "RequireOperatorIdentity",
                policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId)));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The real production mapping - no duplicated route or handler logic.
        app.MapConversationsEndpoints();

        await app.StartAsync();
        return app;
    }

    /// <summary>Stands in for `5-05`'s Keycloak bearer plus
    /// <c>OperatorIdentityClaimsTransformation</c>: it produces exactly the two claims the production
    /// pipeline leaves behind, and nothing else. A request without them is unauthenticated, so
    /// `RequireOperatorIdentity` still does its real job.</summary>
    private sealed class StubOperatorAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string OperatorIdHeader = "X-Test-Operator-Id";
        public const string SiteIdHeader = "X-Test-Site-Id";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(OperatorIdHeader, out var operatorId) ||
                !Request.Headers.TryGetValue(SiteIdHeader, out var siteId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(AgoClaimTypes.OperatorId, operatorId.ToString()),
                    new Claim(AgoClaimTypes.SiteId, siteId.ToString()),
                ],
                Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
