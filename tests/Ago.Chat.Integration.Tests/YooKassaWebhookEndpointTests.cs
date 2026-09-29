using System.Net;
using System.Text;
using Ago.Chat.Api.Billing;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ProcessYooKassaWebhook;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-286`: the production `BillingEndpoints.MapYooKassaWebhookEndpoint` mapping - a real Kestrel host
/// on a real ephemeral loopback port, standing in for ЮKassa's own callback, against a real Postgres
/// (`PostgresFixture`). The host is configured exactly like production for the two things this endpoint
/// now depends on: `UseForwardedHeaders` (so the source IP the allowlist checks is the real client's,
/// resolved from `X-Forwarded-For`, not the connection's loopback address) and an
/// <see cref="IYooKassaPaymentsClient"/> that stands in for ЮKassa's Payments API re-query.
///
/// <para><b>What this proves.</b> ЮKassa does not sign its console-configured HTTP notifications
/// (`adr/0025` superseded), so verification is (1) an IP allowlist and (2) a re-query of the payment,
/// acting on the authoritative status - never the notification body. These prove all of it end to end:
/// a request from outside ЮKassa's networks is rejected 403 and never touches the database; a genuine
/// succeeded payment (authoritative re-query) grants the tier even when the notification body claims
/// otherwise; a payment the re-query reports as pending or canceled grants nothing; a forged payment id
/// the re-query does not recognise grants nothing; and a redelivery does not double-apply.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class YooKassaWebhookEndpointTests(PostgresFixture fixture)
{
    // Inside ЮKassa's published 185.71.76.0/27 notification network.
    private const string AllowedSourceIp = "185.71.76.10";

    // TEST-NET-3 (RFC 5737) - deliberately outside every ЮKassa notification range.
    private const string DisallowedSourceIp = "203.0.113.5";

    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Succeeded_AuthoritativeReQuery_Returns200_AndUpdatesTheSiteInOneTransaction_EvenIfTheBodyClaimsOtherwise()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(5, SubscriptionTierBands.Starter);
        // The re-query is authoritative: succeeded + paid, with the saved card id.
        await using var host = await BuildHostAsync(new()
        {
            [paymentId] = new GetPaymentResult.Found(paymentId, "succeeded", Paid: true, PaymentMethodId: "card_abc123"),
        });
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        // The body deliberately LIES, claiming payment.canceled - the endpoint must ignore it and act on
        // the authoritative re-query instead.
        var response = await PostWebhookAsync(client, paymentId, "payment.canceled", AllowedSourceIp);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal(SubscriptionTierBands.Starter, site.Tier);
        Assert.Equal(5, site.SeatLimit);
        var subscription = await verify.BillingSubscriptions.SingleAsync(s => s.YooKassaPaymentId == paymentId, CancellationToken.None);
        // Proves the saved card id came from the authoritative re-query, not the body (which carried none).
        Assert.Equal("card_abc123", subscription.PaymentMethodId);
    }

    [Fact]
    public async Task FromOutsideYooKassasNetworks_Returns403_AndNeverTouchesTheSite()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(5, SubscriptionTierBands.Starter);
        await using var host = await BuildHostAsync(new()
        {
            [paymentId] = new GetPaymentResult.Found(paymentId, "succeeded", Paid: true, PaymentMethodId: "card_abc123"),
        });
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await PostWebhookAsync(client, paymentId, "payment.succeeded", DisallowedSourceIp);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal("free", site.Tier);
        Assert.Equal(2, site.SeatLimit);
        Assert.False(await verify.BillingWebhookEvents.AnyAsync(e => e.YooKassaPaymentId == paymentId, CancellationToken.None));
    }

    [Fact]
    public async Task ReQueriedAsPending_Returns200_LeavesTheSiteFree_AndWritesNoLedgerRow()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(5, SubscriptionTierBands.Starter);
        await using var host = await BuildHostAsync(new()
        {
            [paymentId] = new GetPaymentResult.Found(paymentId, "pending", Paid: false, PaymentMethodId: null),
        });
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        // The body claims success; the authoritative re-query says pending - no grant.
        var response = await PostWebhookAsync(client, paymentId, "payment.succeeded", AllowedSourceIp);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal("free", site.Tier);
        Assert.False(await verify.BillingWebhookEvents.AnyAsync(e => e.YooKassaPaymentId == paymentId, CancellationToken.None));
    }

    [Fact]
    public async Task AForgedPaymentId_TheReQuerySaysNotFound_Returns200_AndNeverTouchesTheSite()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(5, SubscriptionTierBands.Starter);
        // The re-query knows nothing about the forged id (empty stub table -> NotFound).
        await using var host = await BuildHostAsync(new());
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await PostWebhookAsync(client, paymentId, "payment.succeeded", AllowedSourceIp);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal("free", site.Tier);
        Assert.False(await verify.BillingWebhookEvents.AnyAsync(e => e.YooKassaPaymentId == paymentId, CancellationToken.None));
    }

    [Fact]
    public async Task Succeeded_DeliveredTwice_DoesNotDoubleApply()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(10, SubscriptionTierBands.Growth);
        await using var host = await BuildHostAsync(new()
        {
            [paymentId] = new GetPaymentResult.Found(paymentId, "succeeded", Paid: true, PaymentMethodId: "card_abc"),
        });
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var first = await PostWebhookAsync(client, paymentId, "payment.succeeded", AllowedSourceIp);
        var second = await PostWebhookAsync(client, paymentId, "payment.succeeded", AllowedSourceIp);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal(SubscriptionTierBands.Growth, site.Tier);
        Assert.Equal(10, site.SeatLimit);

        var ledgerRows = await verify.BillingWebhookEvents
            .Where(e => e.YooKassaPaymentId == paymentId && e.EventType == "payment.succeeded")
            .ToListAsync(CancellationToken.None);
        Assert.Single(ledgerRows);
    }

    private static async Task<HttpResponseMessage> PostWebhookAsync(
        HttpClient client, string paymentId, string claimedEvent, string sourceIp)
    {
        var body = BuildWebhookBody(paymentId, claimedEvent);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/billing/webhooks/yookassa")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        // Stand in for the gateway's own hop: the test host trusts loopback as a proxy and reads the real
        // client IP from here, exactly as production reads it from NGINX Gateway's forwarded header.
        request.Headers.Add("X-Forwarded-For", sourceIp);
        return await client.SendAsync(request);
    }

    private static string BuildWebhookBody(string paymentId, string claimedEvent) =>
        "{\"event\":\"" + claimedEvent + "\",\"object\":{\"id\":\"" + paymentId + "\"}}";

    private async Task<(SiteId SiteId, string PaymentId)> SeedPendingSubscriptionAsync(int requestedSeats, string tier)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var paymentId = $"pmt_{Guid.NewGuid():N}";

        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        seed.BillingSubscriptions.Add(BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), siteId, paymentId, requestedSeats, tier, 1, 1, Now));
        await seed.SaveChangesAsync(CancellationToken.None);

        return (siteId, paymentId);
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private async Task<TestHost> BuildHostAsync(Dictionary<string, GetPaymentResult> payments)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Mirror production: trust loopback (the test client's connection origin, standing in for the
        // gateway) so X-Forwarded-For becomes the resolved RemoteIpAddress the allowlist checks.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        });

        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IYooKassaPaymentsClient>(new StubYooKassaPaymentsClient(payments));
        builder.Services.AddScoped<IBillingWebhookApplier, BillingWebhookApplier>();
        builder.Services.AddScoped<ProcessYooKassaWebhookHandler>();

        var app = builder.Build();
        app.UseForwardedHeaders();

        // The real production mapping - no duplicated route or handler logic. Only the webhook route,
        // not MapBillingEndpoints()/MapCreateCheckoutSessionEndpoint() - that route needs
        // RequireOperatorIdentity, which this host deliberately never configures (BillingEndpoints' own
        // remarks on why the two routes were split into separate public extension methods).
        app.MapYooKassaWebhookEndpoint();

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new TestHost(app, baseUrl);
    }

    /// <summary>Stands in for ЮKassa's own Payments API re-query: a payment id it knows resolves to the
    /// authoritative object seeded for it, any other id to <see cref="GetPaymentResult.NotFound"/> - the
    /// real client's own answer for an id ЮKassa has no record of. The two write calls are never reached
    /// by the webhook path, so they throw if a test ever wires the host wrong.</summary>
    private sealed class StubYooKassaPaymentsClient(Dictionary<string, GetPaymentResult> payments) : IYooKassaPaymentsClient
    {
        public Task<GetPaymentResult> GetPaymentAsync(string paymentId, CancellationToken cancellationToken) =>
            Task.FromResult(payments.TryGetValue(paymentId, out var result) ? result : new GetPaymentResult.NotFound());

        public Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The webhook path never creates a payment.");

        public Task<CreatePaymentWithTokenResult> CreatePaymentWithTokenAsync(
            CreatePaymentWithTokenRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The webhook path never creates a token payment.");

        public Task<ChargeStoredPaymentMethodResult> ChargeStoredPaymentMethodAsync(
            ChargeStoredPaymentMethodRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The webhook path never charges a stored method.");
    }
}
