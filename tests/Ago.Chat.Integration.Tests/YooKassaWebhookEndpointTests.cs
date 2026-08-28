using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Api.Billing;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ProcessYooKassaWebhook;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Infrastructure.YooKassa;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `13-02`: the production `BillingEndpoints.MapYooKassaWebhookEndpoint` mapping - a real Kestrel host
/// on a real ephemeral loopback port, standing in for ЮKassa's own callback
/// (`TelegramApiClientTests`/`ForwardedHeadersTests`' own established technique, deliberately not
/// <c>UseTestServer()</c> - the point is proving the raw-body HMAC signature this codebase computes
/// server-side against a signature this test computes independently, over a genuine HTTP transport, not
/// an in-memory one), against a real Postgres (`PostgresFixture`).
///
/// <para><b>What this proves, and what it does not.</b> This proves this deployment's own endpoint
/// logic end to end: a correctly signed notification updates `sites.tier`/`seat_limit` inside one
/// transaction, a missing/invalid signature is rejected `401` and never reaches the database, a
/// redelivered `(payment_id, event_type)` pair does not double-apply, and a `payment.canceled`
/// notification leaves the site on the free tier. It does <b>not</b> prove ЮKassa's own real webhook
/// delivery reaches this endpoint, or that the signature scheme implemented here (hex-encoded
/// HMAC-SHA256 over <c>method|url|body</c>) matches what a real ЮKassa notification actually carries -
/// this item's own report states plainly why that Done-when box is unreachable in this
/// environment.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class YooKassaWebhookEndpointTests(PostgresFixture fixture)
{
    private const string WebhookKey = "test-webhook-key-not-real";

    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PaymentSucceeded_WithAValidSignature_Returns200_AndUpdatesTheSiteInOneTransaction()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(5, SubscriptionTierBands.Starter);
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await PostSignedWebhookAsync(client, host.BaseUrl, paymentId, "payment.succeeded", "card_abc123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal(SubscriptionTierBands.Starter, site.Tier);
        Assert.Equal(5, site.SeatLimit);
    }

    [Fact]
    public async Task PaymentSucceeded_WithAMissingSignatureHeader_Returns401_AndNeverTouchesTheSite()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(5, SubscriptionTierBands.Starter);
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = BuildWebhookBody(paymentId, "payment.succeeded", "card_abc123");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/billing/webhooks/yookassa")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        // Deliberately no Webhook-Signature header at all.

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal("free", site.Tier);
        Assert.Equal(1, site.SeatLimit);
        Assert.False(await verify.BillingWebhookEvents.AnyAsync(e => e.YooKassaPaymentId == paymentId, CancellationToken.None));
    }

    [Fact]
    public async Task PaymentSucceeded_WithATamperedSignature_Returns401_AndNeverTouchesTheSite()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(5, SubscriptionTierBands.Starter);
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = BuildWebhookBody(paymentId, "payment.succeeded", "card_abc123");
        var requestUrl = host.BaseUrl.TrimEnd('/') + "/api/v1/billing/webhooks/yookassa";
        // Signed with the wrong key - a real malformed/forged request, not asserted from the
        // verification code alone.
        var wrongSignature = ComputeHexSignature("a-completely-different-key", "POST", requestUrl, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/billing/webhooks/yookassa")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(BillingEndpoints.YooKassaSignatureHeaderName, wrongSignature);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal("free", site.Tier);
        Assert.False(await verify.BillingWebhookEvents.AnyAsync(e => e.YooKassaPaymentId == paymentId, CancellationToken.None));
    }

    [Fact]
    public async Task PaymentSucceeded_DeliveredTwice_DoesNotDoubleApply()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(10, SubscriptionTierBands.Growth);
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var first = await PostSignedWebhookAsync(client, host.BaseUrl, paymentId, "payment.succeeded", "card_abc");
        var second = await PostSignedWebhookAsync(client, host.BaseUrl, paymentId, "payment.succeeded", "card_abc");

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

    [Fact]
    public async Task PaymentCanceled_WithAValidSignature_Returns200_AndLeavesTheSiteOnTheFreeTier()
    {
        var (siteId, paymentId) = await SeedPendingSubscriptionAsync(5, SubscriptionTierBands.Starter);
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await PostSignedWebhookAsync(client, host.BaseUrl, paymentId, "payment.canceled", paymentMethodId: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verify = fixture.CreateDbContext();
        var site = await verify.Sites.SingleAsync(s => s.Id == siteId, CancellationToken.None);
        Assert.Equal("free", site.Tier);
        Assert.Equal(1, site.SeatLimit);
    }

    private async Task<HttpResponseMessage> PostSignedWebhookAsync(
        HttpClient client, string baseUrl, string paymentId, string eventType, string? paymentMethodId)
    {
        var body = BuildWebhookBody(paymentId, eventType, paymentMethodId);
        var requestUrl = baseUrl.TrimEnd('/') + "/api/v1/billing/webhooks/yookassa";
        var signature = ComputeHexSignature(WebhookKey, "POST", requestUrl, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/billing/webhooks/yookassa")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(BillingEndpoints.YooKassaSignatureHeaderName, signature);

        return await client.SendAsync(request);
    }

    private static string BuildWebhookBody(string paymentId, string eventType, string? paymentMethodId) =>
        paymentMethodId is null
            ? "{\"event\":\"" + eventType + "\",\"object\":{\"id\":\"" + paymentId + "\"}}"
            : "{\"event\":\"" + eventType + "\",\"object\":{\"id\":\"" + paymentId
                + "\",\"payment_method\":{\"id\":\"" + paymentMethodId + "\"}}}";

    /// <summary>Independently reimplements <see cref="YooKassaWebhookSignatureVerifier"/>'s own
    /// algorithm - a test that imported and called the production verifier to produce its own "valid"
    /// signature would prove nothing beyond "this method agrees with itself".</summary>
    private static string ComputeHexSignature(string webhookKey, string httpMethod, string url, string rawBody)
    {
        var canonical = $"{httpMethod}|{url}|{rawBody}";
        var key = Encoding.UTF8.GetBytes(webhookKey);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash);
    }

    private async Task<(SiteId SiteId, string PaymentId)> SeedPendingSubscriptionAsync(int requestedSeats, string tier)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var paymentId = $"pmt_{Guid.NewGuid():N}";

        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        seed.BillingSubscriptions.Add(BillingSubscription.Create(
            new BillingSubscriptionId(Guid.NewGuid()), siteId, paymentId, requestedSeats, tier, Now));
        await seed.SaveChangesAsync(CancellationToken.None);

        return (siteId, paymentId);
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private async Task<TestHost> BuildHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IYooKassaWebhookSignatureVerifier>(
            new YooKassaWebhookSignatureVerifier(new YooKassaOptions { WebhookKey = WebhookKey }));
        builder.Services.AddScoped<IBillingWebhookApplier, BillingWebhookApplier>();
        builder.Services.AddScoped<ProcessYooKassaWebhookHandler>();

        var app = builder.Build();

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
}
