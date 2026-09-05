using Ago.Chat.Api.Auth;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Application.UseCases.RequestSiteExport;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `3-05`'s Done-when: both real call sites are actually wired to a limiter check, not just
/// `IRateLimiter` existing unused - real Redis and real Postgres (`SiteCachingFixture`, already the
/// right combination for a real `GetSiteConfigByPublicKeyHandler`).
/// </summary>
[Collection(SiteCachingCollection.Name)]
public sealed class RateLimitingTests(SiteCachingFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task SendVisitorMessageHandler_OnceThePerVisitorBucketIsExhausted_DeniesFurtherSends()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await db.SaveChangesAsync();
        }

        var limiter = CreateLimiter();
        // Capacity 1, refill slow enough that a second immediate call cannot have refilled.
        var options = new MessageSendRateLimitOptions { PerVisitorCapacity = 1, PerVisitorRefillPerSecond = 0.001, PerSiteCapacity = 1000, PerSiteRefillPerSecond = 1000 };
        await using var db2 = fixture.CreateDbContext();
        var handler = new SendVisitorMessageHandler(
            new ConversationRepository(db2), limiter, options, new SynchronousMessagePipeline(fixture.DataSource));

        var first = await handler.HandleAsync(new SendVisitorMessage(conversationId, visitorId, "one"), CancellationToken.None);
        var second = await handler.HandleAsync(new SendVisitorMessage(conversationId, visitorId, "two"), CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsFailure);
        Assert.Equal("Message.RateLimited", second.Error!.Value.Code);
    }

    [Fact]
    public async Task VisitorSessionEndpoint_OnceThePerSiteBucketIsExhausted_Returns429WithARetryAfterHeader()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, publicKey, []));
            await db.SaveChangesAsync();
        }

        var limiter = CreateLimiter();
        var rateLimitOptions = Options.Create(new VisitorSessionRateLimitOptions { PerSiteCapacity = 1, PerSiteRefillPerSecond = 0.001 });
        var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(fixture.CreateDbContext()), new RedisCache(
            fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisCache>.Instance));
        var tokens = new JwtTokenService(TestSigningKeys.Ring(), "test-issuer", new SystemClock());

        var (firstStatus, firstHeader) = await Invoke();
        var (secondStatus, secondHeader) = await Invoke();

        Assert.Equal(StatusCodes.Status201Created, firstStatus);
        Assert.True(string.IsNullOrEmpty(firstHeader));
        Assert.Equal(StatusCodes.Status429TooManyRequests, secondStatus);
        Assert.False(string.IsNullOrEmpty(secondHeader));

        async Task<(int StatusCode, string? RetryAfterHeader)> Invoke()
        {
            // Result.ExecuteAsync (both Created<T> and ProblemHttpResult) resolves services off
            // HttpContext.RequestServices to serialize the response - DefaultHttpContext leaves it
            // null by default, since this is normally supplied by the real ASP.NET Core pipeline.
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions()));
            var httpContext = new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider(),
                Response = { Body = new MemoryStream() },
            };
            var result = await AuthEndpoints.HandleVisitorSessionAsync(
                new AuthEndpoints.VisitorSessionRequest(publicKey),
                getSite, new SiteInstallationSignalRepository(fixture.DataSource), limiter, rateLimitOptions,
                new UuidV7Generator(), new SystemClock(), tokens, httpContext, CancellationToken.None);
            await result.ExecuteAsync(httpContext);
            return (httpContext.Response.StatusCode, httpContext.Response.Headers.RetryAfter.FirstOrDefault());
        }
    }

    // `16-03`: a third real-limiter case in this file, the same shape as the two above - a real
    // RedisRateLimiter, a capacity exhausted by real calls, and an assertion on the real rejection
    // rather than on the limiter's own configured numbers.
    [Fact]
    public async Task RequestSiteExportHandler_OnceThePerSiteBucketIsExhausted_DeniesFurtherExports()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: "subject-rate-limit-test"));
            var roleId = Guid.NewGuid();
            db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Admin", Permissions = [Permission.SiteExport.Value] });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await db.SaveChangesAsync();
        }

        var limiter = CreateLimiter();
        // Capacity 1, refill slow enough that a third call within the same test cannot have refilled.
        var rateLimitOptions = new SiteExportRateLimitOptions { PerSiteCapacity = 1, PerSiteRefillPerSecond = 0.001 };
        var exportRequests = new ExportRequestRepository(fixture.DataSource);

        async Task<Ago.Platform.Kernel.Result<Guid>> InvokeAsync()
        {
            await using var db = fixture.CreateDbContext();
            var handler = new RequestSiteExportHandler(
                exportRequests, limiter, new PermissionChecker(db), rateLimitOptions, new UuidV7Generator(), new SystemClock());
            return await handler.HandleAsync(new RequestSiteExport(siteId, operatorId), CancellationToken.None);
        }

        var first = await InvokeAsync();
        var second = await InvokeAsync();

        Assert.True(first.IsSuccess);
        Assert.True(second.IsFailure);
        Assert.Equal("Export.RateLimited", second.Error!.Value.Code);
    }

    private RedisRateLimiter CreateLimiter() => new(
        fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisRateLimiter>.Instance);
}
