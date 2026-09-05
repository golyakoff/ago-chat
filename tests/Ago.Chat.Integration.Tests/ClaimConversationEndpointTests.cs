using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Conversations;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AssignConversation;
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
/// `23-04`'s own new route over real HTTP, against a real Postgres - the same
/// production-mapping-not-duplicated-logic shape <see cref="MarkConversationReadEndpointTests"/>
/// already establishes for `/read`, applied to `POST /api/v1/conversations/{id}/claim`. What this file
/// proves that <see cref="AssignConversationHandlerTests"/>/<see cref="AssignConversationConcurrencyTests"/>
/// cannot: that the route is actually wired (`ConversationsEndpoints.MapConversationsEndpoints`), gated
/// by the same <c>RequireOperatorIdentity</c> policy the hub uses, and that a cross-tenant claim
/// attempt through this route specifically - not only through <c>OperatorHub.JoinConversationAsync</c> -
/// comes back <c>404</c>, per this item's own Done-when: "An operator of another tenant cannot claim by
/// id - the `17-01` guard, asserted through the new route as well as through the hub."
/// </summary>
[Collection(PostgresCollection.Name)]
public class ClaimConversationEndpointTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task AnOperatorWithPermission_ClaimsAWaitingConversation_AndChargesCapacity()
    {
        var seed = await SeedAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, seed.OperatorId, seed.SiteId);

        var response = await client.PostAsync($"/api/v1/conversations/{seed.ConversationId.Value}/claim", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        var row = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == seed.ConversationId);
        Assert.Equal(ConversationState.Assigned, row.State);
        Assert.Equal(seed.OperatorId, row.OperatorId);
        Assert.True(row.HoldsCapacityClaim);

        var interval = await verify.ConversationAssignments.AsNoTracking().SingleAsync(i => i.ConversationId == seed.ConversationId);
        Assert.Equal(ConversationAssignmentSource.Taken, interval.Source);

        var activeChats = await verify.Operators.AsNoTracking()
            .Where(o => o.Id == seed.OperatorId)
            .Select(o => EF.Property<int>(o, "active_chats"))
            .SingleAsync();
        Assert.Equal(1, activeChats);
    }

    /// <summary>
    /// `17-01`'s guard, asserted through this route rather than through `AssignConversationHandler`
    /// directly - the item's own Done-when line. The caller genuinely holds `conversation:assign`, on
    /// their own site; the conversation named by id belongs to a different one. Info-hiding: another
    /// tenant's row must read exactly like one that does not exist.
    /// </summary>
    [Fact]
    public async Task AnOperatorOfAnotherTenant_CannotClaimByRoute_AndGets404()
    {
        var seed = await SeedAsync();
        var otherSiteId = new SiteId(Guid.NewGuid());
        var otherOperatorId = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            var roleId = Guid.NewGuid();
            db.Sites.Add(new Site(otherSiteId, $"site_{otherSiteId.Value:N}", []));
            db.Operators.Add(new Operator(otherOperatorId, otherSiteId, OperatorStatus.Online, capacity: 5));
            db.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = otherSiteId,
                Name = "Operator",
                Permissions = [Permission.ConversationAssign.Value],
            });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = otherOperatorId, RoleId = roleId });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, otherOperatorId, otherSiteId);

        var response = await client.PostAsync($"/api/v1/conversations/{seed.ConversationId.Value}/claim", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("Conversation.NotFound", problem!.Type);

        await using var verify = fixture.CreateDbContext();
        var row = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == seed.ConversationId);
        Assert.Equal(ConversationState.Waiting, row.State);
        Assert.Null(row.OperatorId);
        var activeChats = await verify.Operators.AsNoTracking()
            .Where(o => o.Id == otherOperatorId)
            .Select(o => EF.Property<int>(o, "active_chats"))
            .SingleAsync();
        Assert.Equal(0, activeChats);
    }

    private sealed record ProblemBody(string Type, string Title, int Status);

    private sealed record SeedResult(SiteId SiteId, VisitorId VisitorId, OperatorId OperatorId, ConversationId ConversationId);

    private async Task<SeedResult> SeedAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
        seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        seed.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Operator",
            Permissions = [Permission.ConversationAssign.Value],
        });
        seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        seed.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));

        await seed.SaveChangesAsync(CancellationToken.None);
        return new SeedResult(siteId, visitorId, operatorId, conversationId);
    }

    private static HttpClient CreateClient(WebApplication host, OperatorId operatorId, SiteId siteId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(StubOperatorAuthHandler.OperatorIdHeader, operatorId.Value.ToString());
        client.DefaultRequestHeaders.Add(StubOperatorAuthHandler.SiteIdHeader, siteId.Value.ToString());
        return client;
    }

    /// <summary>Same registration list as <see cref="MarkConversationReadEndpointTests"/>'s own test
    /// host, plus <see cref="IOperatorCapacity"/>/<see cref="IUnitOfWork"/> - unlike that file, this one
    /// actually calls `/claim`, so `AssignConversationHandler`'s own dependencies must resolve for real,
    /// not merely satisfy minimal API's endpoint-metadata inference.</summary>
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
        builder.Services.AddScoped<IConversationAssignmentLog, ConversationAssignmentLog>();
        builder.Services.AddScoped<IOperatorCapacity, OperatorCapacityStore>();
        builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        builder.Services.AddScoped<AssignConversationHandler>();
        builder.Services.AddScoped<MarkConversationReadHandler>();
        // The other handlers `MapConversationsEndpoints` references - registered even though no test
        // here calls their routes, because minimal APIs infer an unregistered complex parameter as a
        // *body* parameter and takes the whole test host down otherwise (`MarkConversationReadEndpointTests`'s
        // own comment on this same list, found the same way).
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
        builder.Services.AddScoped<IChannelIdentityRepository, ChannelIdentityRepository>();
        builder.Services.AddScoped<GetVisitorHistoryHandler>();
        builder.Services.AddScoped<IConversationSearchStore, ConversationSearchStore>();
        builder.Services.AddScoped<SearchConversationsHandler>();
        builder.Services.AddScoped<IOperatorAnalyticsReadStore, OperatorAnalyticsReadStore>();
        builder.Services.AddScoped<GetOperatorAnalyticsForSiteHandler>();
        // `23-18`: `MapConversationsEndpoints` now also maps `GET .../analytics/me` -
        // `GetOwnAnalyticsForOperatorHandler` must resolve as a registered service for the same
        // "minimal API infers an unregistered complex parameter as a body parameter" reason every
        // other handler in this list is here for, even though no test in this file calls that route.
        builder.Services.AddScoped<GetOwnAnalyticsForOperatorHandler>();
        builder.Services.AddScoped<IModuleFlowReadStore, ModuleFlowReadStore>();
        builder.Services.AddSingleton(new ModuleFlowReportOptions { ModuleKey = "test-module" });
        builder.Services.AddScoped<GetModuleFlowReportForSiteHandler>();
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<TransferConversationHandler>();
        builder.Services.AddScoped<IConversionReportReadStore, ConversionReportReadStore>();
        builder.Services.AddScoped<GetConversionReportForSiteHandler>();
        builder.Services.AddScoped<SetConversationOutcomeHandler>();
        builder.Services.AddScoped<GetConversationOutcomeHandler>();
        builder.Services.AddScoped<ITagBreakdownReadStore, TagBreakdownReadStore>();
        builder.Services.AddScoped<GetTagBreakdownReportForSiteHandler>();
        // `23-19`: `MapConversationsEndpoints` now also maps `GET .../channel-deliveries` - the same
        // "registered even though no test here calls it" reasoning as every other handler on this
        // page (see the comment above `GetOperatorQueueHandler`'s own registration).
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

        app.MapConversationsEndpoints();

        await app.StartAsync();
        return app;
    }

    /// <summary>Same stub as <see cref="MarkConversationReadEndpointTests"/>'s own - see its remarks.</summary>
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
