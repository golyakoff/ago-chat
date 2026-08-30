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
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-11`: the production `AvitoWebhookEndpoints.MapAvitoWebhookEndpoints` mapping - a real Kestrel host
/// on a real ephemeral loopback port, standing in for Avito's own Messenger API delivery, against a real
/// Postgres (<see cref="PostgresFixture"/>) and a real <see cref="ReceiveChannelMessageHandler"/> chain -
/// <see cref="VkWebhookEndpointsTests"/>'s own established technique for this project's channel webhooks,
/// simpler here than VK's own version: Avito's own webhook receiver makes no outbound call back to the
/// provider (no confirmation handshake, unlike VK), so there is no fake-provider host to stand up.
///
/// <para>What this does <b>not</b> prove: that a real Avito seller account's own Messenger API delivery
/// actually reaches this endpoint over the public internet, or that the field names this item assumed for
/// <c>payload.value</c>'s own nesting are Avito's true shape - <c>AvitoDtos.cs</c>'s own honesty note, and
/// this item's own report, say plainly why that remains unverified in this environment. It also does not
/// prove anything about the undocumented <c>x-avito-messenger-signature</c> header
/// (<c>AvitoWebhookEndpoints</c>'s own remarks on why this item built a different mechanism instead).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AvitoWebhookEndpointsTests(PostgresFixture fixture)
{
    private const string CorrectSecret = "correct-webhook-secret";
    private const string PlaintextToken = "test-avito-access-token-not-a-real-secret";
    private const long WebhookOwnerUserId = 94235311;

    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Message_WithTheCorrectSecret_ReturnsOk_AndTheMessageReachesPostgresThroughTheRealHandlerChain()
    {
        var (siteId, credentialId) = await SeedActiveCredentialAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await PostMessageAsync(client, credentialId, CorrectSecret, authorId: 111, chatId: "chat-1", text: "здравствуйте, актуально?");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadMessageBodyAsync(siteId);
        Assert.Equal("здравствуйте, актуально?", body);
    }

    [Fact]
    public async Task Message_WithAWrongSecret_Returns401()
    {
        var (_, credentialId) = await SeedActiveCredentialAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await PostMessageAsync(client, credentialId, "a-completely-wrong-secret", authorId: 111, chatId: "chat-1", text: "hi");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Message_WithNoSecret_Returns401()
    {
        var (_, credentialId) = await SeedActiveCredentialAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsync($"webhooks/avito/{credentialId.Value}", JsonBody(MessagePayload(111, "chat-1", "hi")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnyDelivery_ForAnUnknownCredentialId_Returns404()
    {
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await PostMessageAsync(client, new ChannelCredentialId(Guid.NewGuid()), CorrectSecret, authorId: 111, chatId: "chat-1", text: "hi");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Info-hiding, the same as <c>MaxWebhookEndpoints</c>'/<c>VkWebhookEndpoints</c>' own
    /// precedent: a revoked credential's own URL reads exactly like one that was never
    /// registered.</summary>
    [Fact]
    public async Task AnyDelivery_ForARevokedCredential_Returns404()
    {
        var (_, credentialId) = await SeedActiveCredentialAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var repository = new ChannelCredentialRepository(db);
            var credential = await repository.GetByIdAsync(credentialId, CancellationToken.None);
            credential!.Revoke();
            await repository.SaveAsync(credential, CancellationToken.None);
        }

        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await PostMessageAsync(client, credentialId, CorrectSecret, authorId: 111, chatId: "chat-1", text: "hi");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The one rule with no direct MAX/Telegram equivalent, proven end to end through the real
    /// HTTP route - <c>AvitoInboundMessageParser</c>'s own remarks on why the seller's own outgoing
    /// message (<c>author_id == user_id</c>) must never reach <see cref="ReceiveChannelMessageHandler"/>
    /// at all.</summary>
    [Fact]
    public async Task Message_ForTheSellersOwnOutgoingEcho_ReturnsOk_ButWritesNothingToTheDatabase()
    {
        var (siteId, credentialId) = await SeedActiveCredentialAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await PostMessageAsync(
            client, credentialId, CorrectSecret, authorId: WebhookOwnerUserId, chatId: "chat-1", text: "an operator's own reply");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await ReadMessageBodyAsync(siteId));
    }

    /// <summary>This item's own scope cut, proven end to end through the real route -
    /// <c>AvitoInboundMessageParser</c>'s own remarks on why an <c>a2u</c> chat (with Avito itself, not a
    /// customer) is filtered out.</summary>
    [Fact]
    public async Task Message_ForAnAvitoSystemChat_ReturnsOk_ButWritesNothingToTheDatabase()
    {
        var (siteId, credentialId) = await SeedActiveCredentialAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsync(
            $"webhooks/avito/{credentialId.Value}?{AvitoWebhookEndpoints.SecretQueryParamName}={CorrectSecret}",
            JsonBody(MessagePayload(111, "chat-1", "hi", chatType: "a2u")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await ReadMessageBodyAsync(siteId));
    }

    [Fact]
    public async Task ADeliveryWithAnUnrecognizedPayloadType_ReturnsOk_WithoutTouchingTheDatabase()
    {
        var (siteId, credentialId) = await SeedActiveCredentialAsync();
        await using var host = await BuildHostAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };

        var response = await client.PostAsync(
            $"webhooks/avito/{credentialId.Value}?{AvitoWebhookEndpoints.SecretQueryParamName}={CorrectSecret}",
            JsonBody(new { id = "env-1", version = "v1.1", timestamp = 123, payload = new { type = "something_else", value = (object?)null } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await ReadMessageBodyAsync(siteId));
    }

    private static async Task<HttpResponseMessage> PostMessageAsync(
        HttpClient client, ChannelCredentialId credentialId, string secret, long authorId, string chatId, string text) =>
        await client.PostAsync(
            $"webhooks/avito/{credentialId.Value}?{AvitoWebhookEndpoints.SecretQueryParamName}={Uri.EscapeDataString(secret)}",
            JsonBody(MessagePayload(authorId, chatId, text)));

    private static object MessagePayload(long authorId, string chatId, string text, string chatType = "u2i") => new
    {
        id = "env-1",
        version = "v1.1",
        timestamp = 123,
        payload = new
        {
            type = "message",
            value = new
            {
                id = $"msg-{Guid.NewGuid():N}",
                chat_id = chatId,
                chat_type = chatType,
                author_id = authorId,
                user_id = WebhookOwnerUserId,
                item_id = 555,
                type = "text",
                content = new { text },
                created = 123,
            },
        },
    };

    private static StringContent JsonBody(object payload) =>
        new(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private async Task<string?> ReadMessageBodyAsync(SiteId siteId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT m.body FROM messages m JOIN conversations c ON c.id = m.conversation_id WHERE c.site_id = @siteId", connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<(SiteId SiteId, ChannelCredentialId CredentialId)> SeedActiveCredentialAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var credentialId = new ChannelCredentialId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync(CancellationToken.None);

        var webhookSecretHash = SHA256.HashData(Encoding.UTF8.GetBytes(CorrectSecret));
        var credential = ChannelCredential.Register(
            credentialId, siteId, ChannelKind.Avito, Cipher.Encrypt(PlaintextToken), webhookSecretHash, Now,
            providerAccountId: WebhookOwnerUserId.ToString());

        var repository = new ChannelCredentialRepository(db);
        await repository.SaveAsync(credential, CancellationToken.None);

        return (siteId, credentialId);
    }

    // One cipher instance per test method (xUnit constructs a fresh AvitoWebhookEndpointsTests per
    // [Fact]) - VkWebhookEndpointsTests' own precedent for why the seeded key and BuildHostAsync's own
    // DI-registered key must be the identical instance.
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

    private async Task<TestHost> BuildHostAsync()
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

        var app = builder.Build();

        // The real production mapping - no duplicated route or handler logic, the same discipline
        // YooKassaWebhookEndpointTests'/VkWebhookEndpointsTests' own BuildHostAsync states for itself.
        app.MapAvitoWebhookEndpoints();

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new TestHost(app, baseUrl);
    }
}
