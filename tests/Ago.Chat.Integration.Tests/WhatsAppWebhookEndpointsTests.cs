using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Api.Channels;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Infrastructure.WhatsApp;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-10`: the production `WhatsAppWebhookEndpoints.MapWhatsAppWebhookEndpoints` mapping - a real Kestrel
/// host on a real ephemeral loopback port, against a real Postgres (<see cref="PostgresFixture"/>) and a
/// real <see cref="ReceiveChannelMessageHandler"/> chain, the identical technique
/// <see cref="VkWebhookEndpointsTests"/> already establishes for a channel's own webhook route.
///
/// <para><b>What this item's own report needs proven, that VK's/MAX's own webhook tests did not have to
/// prove:</b> the GET verification handshake's own exact shape (query-parameter based, unlike VK's own
/// POST-body confirmation event), and that a POST delivery's tenant is resolved from the payload's own
/// <c>phone_number_id</c> rather than a <c>{credentialId}</c> path segment - the one thing every other
/// channel's webhook test never had to exercise, because every other channel's own route carries that
/// segment. <see cref="WhatsAppWebhookEndpoints"/>'s own remarks have the full reasoning for why WhatsApp's
/// shape differs this much from every precedent.</para>
///
/// <para>What this does <b>not</b> prove: that a real Meta App's own webhook delivery actually reaches this
/// endpoint over the public internet, or that the field names this item assumed for the inbound envelope
/// are Meta's true shape at delivery time - though unlike `14-02`'s MAX and `14-08`'s VK, this item's own
/// research came directly from Meta's own current documentation (developers.facebook.com was reachable),
/// not a third-party reconstruction, so the confidence here is higher than either precedent's own honesty
/// note could claim for itself.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WhatsAppWebhookEndpointsTests(PostgresFixture fixture)
{
    private const string AppSecret = "test-app-secret-not-a-real-secret";
    private const string VerifyToken = "test-verify-token-not-a-real-secret";
    private const string PlaintextToken = "test-system-user-token-not-a-real-secret";

    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Verification_WithTheCorrectToken_ReturnsTheChallengeAsPlainText()
    {
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.GetAsync(
            $"webhooks/whatsapp?hub.mode=subscribe&hub.verify_token={VerifyToken}&hub.challenge=1234567890");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1234567890", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Verification_WithAWrongToken_ReturnsForbidden()
    {
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.GetAsync(
            "webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=a-completely-wrong-token&hub.challenge=1234567890");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Verification_WhenNoVerifyTokenIsConfigured_ReturnsNotFound()
    {
        await using var host = await BuildHostAsync(configureWhatsApp: false);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.GetAsync(
            "webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=anything&hub.challenge=1234567890");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delivery_WithACorrectSignatureAndAKnownPhoneNumberId_ReturnsOk_AndTheMessageReachesPostgresThroughTheRealHandlerChain()
    {
        var (siteId, phoneNumberId) = await SeedActiveCredentialAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = InboundMessageJson(phoneNumberId, from: "16505551234", text: "hello from a real WhatsApp number", id: "wamid.1");
        using var request = SignedRequest(body);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var storedBody = await ReadMessageBodyAsync(siteId);
        Assert.Equal("hello from a real WhatsApp number", storedBody);
    }

    [Fact]
    public async Task Delivery_WithAnIncorrectSignature_ReturnsUnauthorized_AndWritesNothing()
    {
        var (siteId, phoneNumberId) = await SeedActiveCredentialAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = InboundMessageJson(phoneNumberId, from: "16505551234", text: "an attacker's own forged delivery", id: "wamid.2");
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/whatsapp") { Content = content };
        request.Headers.Add(WhatsAppWebhookEndpoints.SignatureHeaderName, "sha256=0000000000000000000000000000000000000000000000000000000000000000");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(await ReadMessageBodyAsync(siteId));
    }

    [Fact]
    public async Task Delivery_WhenNoAppSecretIsConfigured_ReturnsUnauthorized()
    {
        var (_, phoneNumberId) = await SeedActiveCredentialAsync();
        await using var host = await BuildHostAsync(configureWhatsApp: false);
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = InboundMessageJson(phoneNumberId, from: "16505551234", text: "hello", id: "wamid.3");
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/whatsapp") { Content = content };
        request.Headers.Add(WhatsAppWebhookEndpoints.SignatureHeaderName, "sha256=irrelevant");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The one rule with no MAX/Telegram/VK equivalent, proven end to end through the real HTTP route -
    /// <see cref="IChannelCredentialRepository.GetActiveByProviderAccountIdAsync"/>'s own remarks on why a
    /// delivery for a <c>phone_number_id</c> nobody on this deployment has connected must be acknowledged
    /// (Meta retries a non-2xx) rather than rejected, and must never be mistaken for a different tenant's
    /// message.
    /// </summary>
    [Fact]
    public async Task Delivery_ForAnUnknownPhoneNumberId_ReturnsOk_ButWritesNothing()
    {
        var (siteId, _) = await SeedActiveCredentialAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = InboundMessageJson(
            $"unconnected-{Guid.NewGuid():N}", from: "16505551234", text: "a number nobody connected", id: "wamid.4");
        using var request = SignedRequest(body);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await ReadMessageBodyAsync(siteId));
    }

    /// <summary><see cref="WhatsAppEntry"/>'s own remarks: Meta's own envelope is natively a batch
    /// container - proven here through the real route, not only at the parser level
    /// (<see cref="WhatsAppInboundMessageParserTests"/>'s own scope).</summary>
    [Fact]
    public async Task Delivery_WithTwoMessagesInOneBatch_WritesBothThroughTheRealHandlerChain()
    {
        var (siteId, phoneNumberId) = await SeedActiveCredentialAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var body = $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "id": "entry-1",
              "changes": [
                {
                  "field": "messages",
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": { "display_phone_number": "15555555555", "phone_number_id": "{{phoneNumberId}}" },
                    "messages": [
                      { "from": "16505551234", "id": "wamid.batch-1", "timestamp": "1700000000", "type": "text", "text": { "body": "first message" } },
                      { "from": "16505551234", "id": "wamid.batch-2", "timestamp": "1700000001", "type": "text", "text": { "body": "second message" } }
                    ]
                  }
                }
              ]
            }
          ]
        }
        """;
        using var request = SignedRequest(body);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var count = await CountMessagesAsync(siteId);
        Assert.Equal(2, count);
    }

    private static string InboundMessageJson(string phoneNumberId, string from, string text, string id) => $$"""
    {
      "object": "whatsapp_business_account",
      "entry": [
        {
          "id": "entry-1",
          "changes": [
            {
              "field": "messages",
              "value": {
                "messaging_product": "whatsapp",
                "metadata": { "display_phone_number": "15555555555", "phone_number_id": "{{phoneNumberId}}" },
                "messages": [
                  { "from": "{{from}}", "id": "{{id}}", "timestamp": "1700000000", "type": "text", "text": { "body": "{{text}}" } }
                ]
              }
            }
          ]
        }
      ]
    }
    """;

    private static HttpRequestMessage SignedRequest(string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), bodyBytes));

        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/whatsapp") { Content = content };
        request.Headers.Add(WhatsAppWebhookEndpoints.SignatureHeaderName, $"sha256={signature}");
        return request;
    }

    private async Task<string?> ReadMessageBodyAsync(SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT m.body FROM messages m JOIN conversations c ON c.id = m.conversation_id WHERE c.site_id = @siteId", connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<long> CountMessagesAsync(SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM messages m JOIN conversations c ON c.id = m.conversation_id WHERE c.site_id = @siteId", connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// A fresh <c>phone_number_id</c> per call, not a shared constant - <see cref="PostgresFixture"/>'s
    /// own remarks describe one Postgres container shared across every test in this class with no
    /// truncation between them, so a literal id reused across test methods would collide with this
    /// item's own new <c>ux_channel_credentials_kind_provideraccountid_active</c> index
    /// (<c>ChannelCredentialConfiguration</c>'s own remarks) - the same reason every other seeded id in
    /// this file is a fresh <see cref="Guid"/>, applied to the one column that index is new for.
    /// </summary>
    private async Task<(SiteId SiteId, string PhoneNumberId)> SeedActiveCredentialAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var credentialId = new ChannelCredentialId(Guid.NewGuid());
        var phoneNumberId = $"106540{Guid.NewGuid():N}"[..15];

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync(CancellationToken.None);

        var credential = ChannelCredential.Register(
            credentialId, siteId, ChannelKind.WhatsApp, Cipher.Encrypt(PlaintextToken), [1, 2, 3], Now,
            providerAccountId: phoneNumberId);

        var repository = new ChannelCredentialRepository(db);
        await repository.SaveAsync(credential, CancellationToken.None);

        return (siteId, phoneNumberId);
    }

    // One cipher instance per test method (xUnit constructs a fresh WhatsAppWebhookEndpointsTests per
    // [Fact]) - VkWebhookEndpointsTests' own precedent for why the seeded credential and the host's own
    // DI registration must share the identical key.
    private ChannelCredentialCipher? _cipher;

    private ChannelCredentialCipher Cipher => _cipher ??=
        new ChannelCredentialCipher(new ChannelCredentialCipherOptions
        {
            CredentialEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        });

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private async Task<TestHost> BuildHostAsync(bool configureWhatsApp = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddScoped<IChannelCredentialRepository, ChannelCredentialRepository>();
        builder.Services.AddScoped<IChannelCredentialCipher>(_ => Cipher);
        builder.Services.AddScoped<IChannelIdentityRepository, ChannelIdentityRepository>();
        builder.Services.AddScoped<IVisitorRepository, VisitorRepository>();
        builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
        builder.Services.AddScoped<IRateLimiter, FakeRateLimiter>();
        builder.Services.AddSingleton(new MessageSendRateLimitOptions());
        builder.Services.AddSingleton<IMessagePipeline>(_ => new SynchronousMessagePipeline(fixture.DataSource));
        builder.Services.AddScoped<StartConversationHandler>();
        builder.Services.AddScoped<SendVisitorMessageHandler>();
        builder.Services.AddScoped<ReceiveChannelMessageHandler>();
        builder.Services.AddSingleton(Options.Create(configureWhatsApp
            ? new WhatsAppBotApiOptions { AppSecret = AppSecret, VerifyToken = VerifyToken }
            : new WhatsAppBotApiOptions()));

        var app = builder.Build();

        // The real production mapping - no duplicated route or handler logic, the same discipline
        // VkWebhookEndpointsTests' own BuildHostAsync states for itself.
        app.MapWhatsAppWebhookEndpoints();

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new TestHost(app, baseUrl);
    }
}
