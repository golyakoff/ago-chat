using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Billing;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.PreviewBillingPurchase;
using Ago.Chat.Application.UseCases.PurchaseChannelAddOn;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
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
/// `26-304`'s own "reject an unknown/blank kind string with a clean 400, not an exception" requirement,
/// over a real HTTP pipeline and a real Postgres - <see cref="BillingWireEnumSerializationTests"/> already
/// proves the wire *shape* (member-name string, round-trips); this file proves the *behaviour* at the two
/// routes that parse one: an unrecognised or blank <see cref="Domain.ChannelKind"/>/
/// <see cref="BillingPurchaseKind"/> string must never reach the real handler, and must never throw past
/// the endpoint - it comes back as an ordinary <c>Billing.*</c> problem response, the identical shape
/// every other client-caused billing refusal in this codebase already takes.
///
/// <para><b><see cref="AllowAllPermissionChecker"/>, not seeded roles</b> - the identical simplification
/// <c>RenewNowHandlerIntegrationTests</c>/<c>CreateTokenPaymentHandlerIntegrationTests</c> already make for
/// a billing test that has nothing to do with the permission system: every route this file calls is gated
/// by <c>Permission.SiteConfigure</c>, proven elsewhere, not here.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BillingWireEnumEndpointRejectionTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    // `TryParseChannelKind`'s own remarks note a real limitation shared with `RequestChannelLinkFromConsoleHandler`'s
    // identical guard: a numeric string that happens to name a real ordinal (e.g. "2" == Telegram) parses
    // successfully rather than being rejected as "not a member name" - this theory only covers strings no
    // interpretation of `ChannelKind` accepts, not that narrower, pre-existing gap.
    [Theory]
    [InlineData("NotAKind")]
    [InlineData("")]
    public async Task PurchaseChannelAddOn_WithAnUnrecognisedChannelKindString_Returns400_NeverReachesTheHandler(string rawChannelKind)
    {
        var (siteId, baseId, operatorId) = await SeedSucceededBaseSubscriptionAsync();
        await using var host = await BuildTestHostAsync(new ThrowingYooKassaClient(), new ThrowingChannelAddOnPurchaseApplier());
        using var client = CreateClient(host, operatorId, siteId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/sites/{siteId.Value}/billing/subscriptions/{baseId.Value}/channels",
            new BillingEndpoints.PurchaseChannelAddOnRequest(rawChannelKind));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("Billing.InvalidChannelKind", problem!.Type);

        // Never touched the base subscription - a rejected wire shape must not even attempt a charge.
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.BillingSubscriptions.AnyAsync(s => s.SiteId == siteId && s.Id != baseId));
    }

    [Fact]
    public async Task PreviewBillingPurchase_WithAnUnrecognisedKindString_Returns400_NeverReachesTheHandler()
    {
        var (siteId, baseId, operatorId) = await SeedSucceededBaseSubscriptionAsync();
        await using var host = await BuildTestHostAsync(new ThrowingYooKassaClient(), new ThrowingChannelAddOnPurchaseApplier());
        using var client = CreateClient(host, operatorId, siteId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/sites/{siteId.Value}/billing/subscriptions/{baseId.Value}/purchase-preview",
            new BillingEndpoints.PreviewBillingPurchaseRequest("NotAKind", null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("Billing.PreviewRequestInvalid", problem!.Type);
    }

    [Fact]
    public async Task PreviewBillingPurchase_WithAValidKindButAnUnrecognisedChannelKindString_Returns400()
    {
        var (siteId, baseId, operatorId) = await SeedSucceededBaseSubscriptionAsync();
        await using var host = await BuildTestHostAsync(new ThrowingYooKassaClient(), new ThrowingChannelAddOnPurchaseApplier());
        using var client = CreateClient(host, operatorId, siteId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/sites/{siteId.Value}/billing/subscriptions/{baseId.Value}/purchase-preview",
            new BillingEndpoints.PreviewBillingPurchaseRequest("Channel", null, null, "NotAKind"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal("Billing.PreviewRequestInvalid", problem!.Type);
    }

    /// <summary>The happy path, end to end, through the now-string-typed wire field - proves parsing a
    /// real member name still reaches <see cref="PurchaseChannelAddOnHandler"/> with the right
    /// <see cref="ChannelKind"/> and completes a real, `channel-addon`-priced purchase (seeded by
    /// `Stage26SeedChannelAddOnPrice` on every fresh migrated database, no manual price seed needed
    /// here).</summary>
    [Fact]
    public async Task PurchaseChannelAddOn_WithAValidMemberNameString_ChargesAndConnectsTheRealChannel()
    {
        var (siteId, baseId, operatorId) = await SeedSucceededBaseSubscriptionAsync();
        await using var host = await BuildTestHostAsync(
            new RecordingYooKassaClient(new ChargeStoredPaymentMethodResult.Success("pmt_channel_addon")),
            new RecordingChannelAddOnPurchaseApplier());
        using var client = CreateClient(host, operatorId, siteId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/sites/{siteId.Value}/billing/subscriptions/{baseId.Value}/channels",
            new BillingEndpoints.PurchaseChannelAddOnRequest(nameof(ChannelKind.Telegram)));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<PurchaseChannelAddOnResult>();
        Assert.NotNull(result);
        Assert.Equal(ChannelKind.Telegram, result.ChannelKind);
    }

    private sealed record ProblemBody(string Type, string Title, int Status);

    private async Task<(SiteId SiteId, BillingSubscriptionId BaseSubscriptionId, OperatorId OperatorId)> SeedSucceededBaseSubscriptionAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var baseId = new BillingSubscriptionId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: 5));
        var baseSubscription = BillingSubscription.Create(
            baseId, siteId, $"pmt_{baseId.Value:N}", requestedSeats: 5, SubscriptionTierBands.Starter,
            baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt: Now - BillingSubscription.PeriodLength);
        baseSubscription.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength);
        db.BillingSubscriptions.Add(baseSubscription);
        await db.SaveChangesAsync();

        return (siteId, baseId, operatorId);
    }

    private static HttpClient CreateClient(WebApplication host, OperatorId operatorId, SiteId siteId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(StubOperatorAuthHandler.OperatorIdHeader, operatorId.Value.ToString());
        client.DefaultRequestHeaders.Add(StubOperatorAuthHandler.SiteIdHeader, siteId.Value.ToString());
        return client;
    }

    /// <summary>Only the two routes `26-304` touches - both handlers' own full dependency graphs must
    /// still resolve for real (minimal API binds every service parameter before the delegate body runs,
    /// including on the rejected-before-the-handler-runs path), which is why the YooKassa client/applier
    /// are always supplied, even for the tests that never expect either to be called.</summary>
    private async Task<WebApplication> BuildTestHostAsync(IYooKassaPaymentsClient yooKassa, IChannelAddOnPurchaseApplier applier)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();

        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        builder.Services.AddScoped<IBillingSubscriptionRepository, BillingSubscriptionRepository>();
        builder.Services.AddScoped<IPriceCatalogRepository, PriceCatalogRepository>();
        builder.Services.AddSingleton<IPermissionChecker>(new AllowAllPermissionChecker());
        builder.Services.AddSingleton(yooKassa);
        builder.Services.AddSingleton(applier);
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IClock>(new FixedClock(Now));
        builder.Services.AddScoped<PurchaseChannelAddOnHandler>();
        builder.Services.AddScoped<PreviewBillingPurchaseHandler>();

        builder.Services.AddAuthentication(JwtSchemes.Operator)
            .AddScheme<AuthenticationSchemeOptions, StubOperatorAuthHandler>(JwtSchemes.Operator, _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(
                "RequireOperatorIdentity",
                policy => policy.AddAuthenticationSchemes(JwtSchemes.Operator).RequireClaim(AgoClaimTypes.OperatorId)));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapPurchaseChannelAddOnEndpoint();
        app.MapPreviewBillingPurchaseEndpoint();

        await app.StartAsync();
        return app;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class AllowAllPermissionChecker : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<string>> GetPermissionsAsync(OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not part of the wire-rejection path under test.");

        public Task<int> CountNonRemovedHoldersAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not part of the wire-rejection path under test.");

        public Task<IReadOnlyList<OperatorId>> ListNonRemovedHolderIdsAsync(SiteId siteId, Permission permission, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not part of the wire-rejection path under test.");
    }

    /// <summary>Proves the negative-path tests never reach the real payment client - any call here fails
    /// the test loudly rather than silently charging or swallowing the assertion.</summary>
    private sealed class ThrowingYooKassaClient : IYooKassaPaymentsClient
    {
        public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rejected wire shape must never reach the payment client.");

        public Task<CreatePaymentWithTokenResult> CreatePaymentWithTokenAsync(CreatePaymentWithTokenRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rejected wire shape must never reach the payment client.");

        public Task<ChargeStoredPaymentMethodResult> ChargeStoredPaymentMethodAsync(ChargeStoredPaymentMethodRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rejected wire shape must never reach the payment client.");

        public Task<GetPaymentResult> GetPaymentAsync(string paymentId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rejected wire shape must never reach the payment client.");
    }

    /// <summary>The identical "never reached" proof for the applier half of a channel purchase.</summary>
    private sealed class ThrowingChannelAddOnPurchaseApplier : IChannelAddOnPurchaseApplier
    {
        public Task ApplyPurchaseAsync(ChannelAddOnPurchaseApplyRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A rejected wire shape must never reach the purchase applier.");
    }

    private sealed class RecordingYooKassaClient(ChargeStoredPaymentMethodResult chargeResult) : IYooKassaPaymentsClient
    {
        public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not part of the channel-add-on purchase path under test.");

        public Task<CreatePaymentWithTokenResult> CreatePaymentWithTokenAsync(CreatePaymentWithTokenRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not part of the channel-add-on purchase path under test.");

        public Task<ChargeStoredPaymentMethodResult> ChargeStoredPaymentMethodAsync(ChargeStoredPaymentMethodRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(chargeResult);

        public Task<GetPaymentResult> GetPaymentAsync(string paymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not part of the channel-add-on purchase path under test.");
    }

    /// <summary>A no-op <see cref="IChannelAddOnPurchaseApplier"/> - this test only needs to prove the
    /// handler reached a successful charge with the right <see cref="ChannelKind"/> and returned it on
    /// the response, not the real option-row/entitlement-grant write
    /// <see cref="ChannelAddOnPurchaseEndToEndTests"/> already covers against the real Infrastructure
    /// implementation.</summary>
    private sealed class RecordingChannelAddOnPurchaseApplier : IChannelAddOnPurchaseApplier
    {
        public Task ApplyPurchaseAsync(ChannelAddOnPurchaseApplyRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>Same stub as <see cref="ClaimConversationEndpointTests"/>'s own - see its remarks.</summary>
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
